using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using CAMAPI.DotnetHelper;
using CAMAPI.EventHandler;
using CAMAPI.Project;
using CAMAPI.TechOperation;
using CAMAPI.Technologist;
using EncyPulse.Core;

namespace EncyPulse.Capture;

/// <summary>
/// One handler object registered on every operation of the bound project (ToolpathCalculated) and
/// on the technologist (OperationAdded). Only operation ids are kept; never COM references.
/// </summary>
internal sealed class OperationSubscriptions :
    ICamApiEventHandler,
    ICamApiHandlerTechOperationToolpathCalculated,
    ICamApiHandlerTechnologistOperationAdded
{
    public const string TechIdent = "EncyPulse.Tech";
    public const string OpIdentPrefix = "EncyPulse.Op.";

    private readonly Dispatcher _dispatcher;
    private readonly Log _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _bound = new(StringComparer.OrdinalIgnoreCase);
    private bool _techBound;
    private volatile string _projectId = "";
    private volatile string _projectName = "";

    public OperationSubscriptions(Dispatcher dispatcher, Log log)
    {
        _dispatcher = dispatcher;
        _log = log;
    }

    public bool GetAsyncMode(string interfaceUid) => false;

    public int BoundCount { get { lock (_gate) return _bound.Count; } }

    /// <summary>Hook every operation of the project. Idempotent for the same project unless forced.</summary>
    public void Bind(ComWrapper<ICamApiProject> projectCom, bool force = false)
    {
        lock (_gate)
        {
            var id = projectCom.Id();
            if (!force && _bound.Count > 0 && string.Equals(id, _projectId, StringComparison.OrdinalIgnoreCase))
            {
                _log.Debug($"project {id} already bound");
                return;
            }
            UnbindCore();

            _projectId = id;
            var path = projectCom.FilePath();
            _projectName = string.IsNullOrEmpty(path) ? "Untitled project" : Path.GetFileName(path);

            using var techCom = projectCom.Technologist();
            if (techCom.IsNull)
            {
                _log.Warn("project has no technologist; nothing to hook");
                return;
            }

            try
            {
                var events = Runtime.EventList(typeof(ICamApiHandlerTechnologistOperationAdded));
                techCom.Invoke(t =>
                {
                    t.RegisterHandler(TechIdent, this, events, out var st);
                    Runtime.Throw(st, "RegisterHandler(technologist)");
                });
                _techBound = true;
            }
            catch (Exception ex) { _log.Warn($"OperationAdded hook unavailable: {ex.Message}"); }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var detailed = _log.MinLevel <= LogLevel.Debug;
            int hooked = 0, groups = 0;
            foreach (var opCom in techCom.EnumerateOperations(TCamApiReorderingMode.rmDesigned))
            {
                using (opCom)
                {
                    if (opCom.IsGroup()) { groups++; continue; }
                    if (BindOperation(opCom)) hooked++;
                    if (!detailed) continue; // the state reads are for diagnostics only
                    try
                    {
                        _log.Debug($"state '{opCom.FullName()}': enabled={opCom.Enabled()} calculated={opCom.Calculated()} toolpath={opCom.HasToolpath()} simulated={opCom.Simulated()} error={opCom.IsError()}");
                    }
                    catch (Exception ex) { _log.Debug($"state read failed: {ex.Message}"); }
                }
            }
            _log.Info($"hooked {hooked} operations ({groups} groups skipped) in '{_projectName}' [{_projectId}] in {sw.ElapsedMilliseconds} ms");
            if (sw.ElapsedMilliseconds > 500) _log.Warn($"hooking took {sw.ElapsedMilliseconds} ms on ENCY's thread; consider disabling detailed log");
        }
    }

    private bool BindOperation(ComWrapper<ICamApiTechOperation> opCom)
    {
        var id = opCom.Id();
        if (_bound.Contains(id)) return true;
        try
        {
            var events = Runtime.EventList(typeof(ICamApiHandlerTechOperationToolpathCalculated));
            opCom.Invoke(o =>
            {
                o.RegisterHandler(OpIdentPrefix + id, this, events, out var st);
                Runtime.Throw(st, "RegisterHandler(operation)");
            });
            _bound.Add(id);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"could not hook operation {id}: {ex.Message}");
            return false;
        }
    }

    public void Unbind()
    {
        lock (_gate) UnbindCore();
    }

    private void UnbindCore()
    {
        if (_bound.Count == 0 && !_techBound) return;
        var count = _bound.Count;
        var name = _projectName;
        try
        {
            using var appCom = SystemExtensionFactory.GetApplication();
            using var projectCom = appCom.IsNull ? null : appCom.GetActiveProject();
            if (projectCom != null && !projectCom.IsNull &&
                string.Equals(projectCom.Id(), _projectId, StringComparison.OrdinalIgnoreCase))
            {
                using var techCom = projectCom.Technologist();
                foreach (var id in _bound)
                {
                    try
                    {
                        using var opCom = techCom.GetOperationById(id);
                        if (!opCom.IsNull) opCom.Invoke(o => o.UnregisterHandler(OpIdentPrefix + id, out _));
                    }
                    catch (Exception ex) { _log.Debug($"unhook {id}: {ex.Message}"); }
                }
                if (_techBound)
                {
                    try { techCom.Invoke(t => t.UnregisterHandler(TechIdent, out _)); }
                    catch (Exception ex) { _log.Debug($"unhook technologist: {ex.Message}"); }
                }
            }
            else
            {
                _log.Debug("previous project is no longer active; its handlers go with it");
            }
        }
        catch (Exception ex) { _log.Warn($"unbind: {ex.Message}"); }
        finally
        {
            _bound.Clear();
            _techBound = false;
            _projectId = "";
            _projectName = "";
            _log.Info($"unhooked {count} operations from '{name}'");
        }
    }

    // ---- ENCY callbacks (ENCY thread) -------------------------------------------------------

    public void ToolpathCalculated(string handlerIdent, ICamApiTechOperation operation)
    {
        try
        {
            _log.Debug($"ToolpathCalculated fired ({handlerIdent})");
            using var opCom = ComWrapper.Create(operation);
            if (opCom.IsNull) return;
            var evt = new OperationCalculatedEvent(
                ProjectId: _projectId,
                ProjectName: _projectName,
                OperationId: opCom.Id(),
                Name: opCom.Name(),
                FullName: opCom.FullName(),
                IsGroup: opCom.IsGroup(),
                Calculated: opCom.Calculated(),
                HasToolpath: SafeBool(() => opCom.HasToolpath()),
                At: DateTimeOffset.UtcNow);

            if (!Runtime.Settings.Current.Diagnostics.BackgroundSnapshot)
                evt = evt with { Snapshot = Runtime.TrySnapshot() };

            _dispatcher.Post(evt);
        }
        catch (Exception ex) { _log.Error("ToolpathCalculated handler", ex); }
    }

    public void OperationAdded(string handlerIdent, ICamApiTechOperation operation)
    {
        try
        {
            using var opCom = ComWrapper.Create(operation);
            if (opCom.IsNull || opCom.IsGroup()) return;
            lock (_gate)
            {
                if (BindOperation(opCom)) _log.Info($"hooked new operation '{opCom.FullName()}'");
            }
        }
        catch (Exception ex) { _log.Error("OperationAdded handler", ex); }
    }

    private static bool SafeBool(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }
}
