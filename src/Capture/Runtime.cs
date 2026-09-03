using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.IO;
using CAMAPI.Application;
using CAMAPI.ApplicationMainForm;
using CAMAPI.DotnetHelper;
using CAMAPI.Project;
using CAMAPI.ResultStatus;
using EncyPulse.Core;

namespace EncyPulse.Capture;

/// <summary>
/// Process-wide state of the extension: stores, dispatcher, event subscriptions, tree poll.
/// Started by the Global extension (or lazily by the settings utility), stopped on ENCY shutdown.
/// </summary>
internal static class Runtime
{
    public const string AppHandlerIdent = "EncyPulse.App";

    private static readonly object Gate = new();
    private static bool _started;
    private static Log? _log;
    private static JsonFileStore<Settings>? _settings;
    private static JsonFileStore<NotifyRules>? _rules;
    private static Dispatcher? _dispatcher;
    private static OperationSubscriptions? _subs;
    private static AppEventHandler? _appHandler;
    private static MainFormHandler? _mainFormHandler;
    private static SimulationDetector? _sim;
    private static Timer? _retryTimer;
    private static Timer? _pollTimer;
    private static int _registerAttempts;
    private static int _pollBusy;
    private static int _pollTick;
    private static TreeSnapshot? _lastPoll;
    private static TMainWorkMode? _lastMode;
    private static volatile bool _appRegistered;
    private static volatile bool _mainFormRegistered;
    private static EncyLogTailer? _tailer;
    private static SimulationSession? _simSession;
    private static readonly Dictionary<string, DateTimeOffset> _calcStarted = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset _lastProjectLoad = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastCalcActivity = DateTimeOffset.MinValue;
    private static int _tailFailures;

    public static string DataDir { get; } = ResolveDataDir();

    public static string Version =>
        typeof(Runtime).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static Log Log
    {
        get { lock (Gate) return _log ??= new Log(Path.Combine(DataDir, "pulse.log")); }
    }

    public static JsonFileStore<Settings> Settings
    {
        get { lock (Gate) return _settings ??= CreateSettingsStore(); }
    }

    public static JsonFileStore<NotifyRules> Rules
    {
        get { lock (Gate) return _rules ??= new JsonFileStore<NotifyRules>(Path.Combine(DataDir, "rules.json"), Log); }
    }

    public static Dispatcher? Dispatcher => _dispatcher;
    public static bool IsStarted => _started;

    private static string ResolveDataDir()
    {
        var env = Environment.GetEnvironmentVariable("ENCYPULSE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ENCY SOFTWARE");
        var dir = Path.Combine(root, "EncyPulse");
        // One-time migration from the pre-release name.
        var legacy = Path.Combine(root, "EncyNotify");
        try
        {
            if (!Directory.Exists(dir) && Directory.Exists(legacy))
            {
                Directory.CreateDirectory(dir);
                foreach (var f in new[] { "settings.json", "rules.json" })
                {
                    var src = Path.Combine(legacy, f);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(dir, f), overwrite: false);
                }
            }
        }
        catch { /* start fresh */ }
        return dir;
    }

    private static JsonFileStore<Settings> CreateSettingsStore()
    {
        var store = new JsonFileStore<Settings>(Path.Combine(DataDir, "settings.json"), Log);
        // Every installation gets its own personal code the first time the settings exist.
        if (string.IsNullOrWhiteSpace(store.Current.Channels.Ntfy.Topic))
        {
            store.Update(s => s.Channels.Ntfy.Topic = TopicCode.New());
            Log.Info("generated a personal ntfy code for this installation");
        }
        ApplyDiagnostics(store.Current);
        store.Changed += s =>
        {
            ApplyDiagnostics(s);
            _dispatcher?.RefreshSenders();
        };
        return store;
    }

    public static void ApplyDiagnostics(Settings s) =>
        Log.MinLevel = s.Diagnostics.DebugLog ? LogLevel.Debug : LogLevel.Info;

    /// <summary>
    /// Cheap: creates the worker and stores, registers application hooks. Safe to call twice.
    /// Runs on ENCY's thread during startup, so everything slow (network, log tailing, tree reads)
    /// is deferred to the background worker and timers.
    /// </summary>
    public static void Start()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (Gate)
        {
            if (_started) return;
            Log.Info($"ENCY Pulse {Version} starting; data dir {DataDir}");
            var settings = Settings;
            var rules = Rules;
            _dispatcher = new Dispatcher(
                () => settings.Current,
                () => rules.Current,
                new Outbox(Path.Combine(DataDir, "outbox"), Log),
                WinIdle.GetIdleSeconds,
                Log)
            {
                SnapshotProvider = TrySnapshot,
            };
            _dispatcher.Start();
            _sim = new SimulationDetector(() => TimeSpan.FromMilliseconds(Math.Max(500, settings.Current.Defaults.SimulationQuietMs)));
            _simSession = new SimulationSession(() => TimeSpan.FromSeconds(Math.Max(2, settings.Current.Defaults.SimulationSessionQuietSec)));
            _tailer = new EncyLogTailer(Log);
            _subs = new OperationSubscriptions(_dispatcher, Log);
            _appHandler = new AppEventHandler();
            _mainFormHandler = new MainFormHandler();
            _started = true;
        }

        if (!TryRegisterAppHandlers())
        {
            _retryTimer = new Timer(_ =>
            {
                if (TryRegisterAppHandlers() || Interlocked.Increment(ref _registerAttempts) > 90)
                {
                    _retryTimer?.Dispose();
                    _retryTimer = null;
                    if (!_appRegistered) Log.Error("application handlers could not be registered; notifications are inactive");
                }
            }, null, 2000, 2000);
        }
        _pollTimer = new Timer(Poll, null, 3000, 500);
        var ms = sw.ElapsedMilliseconds;
        if (ms > 200) Log.Warn($"startup took {ms} ms on ENCY's thread (expected < 50 ms)");
        else Log.Debug($"startup took {ms} ms");
    }

    private static bool TryRegisterAppHandlers()
    {
        if (_appRegistered) return true;
        try
        {
            using var appCom = SystemExtensionFactory.GetApplication();
            if (appCom.IsNull) return false;
            var events = EventList(AppEventHandler.HandledInterfaces);
            var handler = _appHandler ?? throw new InvalidOperationException("runtime not started");
            appCom.Invoke(a =>
            {
                a.RegisterHandler(AppHandlerIdent, handler, events, out var st);
                Throw(st, "RegisterHandler(application)");
            });
            _appRegistered = true;
            Log.Info("application handlers registered");

            var started = false;
            try { started = appCom.Invoke(a => a.Started); } catch { /* treat as not started */ }
            if (started) OnApplicationLoaded();
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"application not available yet: {ex.Message}");
            return false;
        }
    }

    /// <summary>ENCY finished loading: the main window exists, a project may already be open.</summary>
    public static void OnApplicationLoaded()
    {
        RegisterMainFormHandler();
        AttachLogTailer();
        BindActiveProject();
    }

    /// <summary>Called on project load events so automatic post-load simulations are not reported.</summary>
    public static void NoteProjectLoaded() => _lastProjectLoad = DateTimeOffset.UtcNow;

    private static void AttachLogTailer()
    {
        if (_tailer == null || !Settings.Current.Diagnostics.TailEncyLog) return;
        try
        {
            using var appCom = SystemExtensionFactory.GetApplication();
            if (appCom.IsNull) return;
            var path = appCom.LogFilePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { Log.Warn($"ENCY log file not found ('{path}'); log tailing disabled"); return; }
            _tailer.Attach(path);
        }
        catch (Exception ex) { Log.Warn($"cannot attach to ENCY log: {ex.Message}"); }
    }

    private static void RegisterMainFormHandler()
    {
        if (_mainFormRegistered || _mainFormHandler == null) return;
        try
        {
            using var appCom = SystemExtensionFactory.GetApplication();
            if (appCom.IsNull) return;
            using var mainFormCom = appCom.MainForm();
            if (mainFormCom.IsNull) { Log.Debug("main form not available yet"); return; }
            var events = EventList(typeof(ICamApiHandlerProgressIndicator));
            var handler = _mainFormHandler;
            mainFormCom.Invoke(f =>
            {
                f.RegisterHandler(MainFormHandler.Ident, handler, events, out var st);
                Throw(st, "RegisterHandler(main form)");
            });
            _mainFormRegistered = true;
            Log.Info("main form progress handler registered");
        }
        catch (Exception ex) { Log.Warn($"main form progress handler unavailable: {ex.Message}"); }
    }

    /// <summary>Subscribe to the operations of the active project (if any). Must run on ENCY's thread.</summary>
    public static void BindActiveProject(ComWrapper<ICamApiApplication>? appCom = null, bool force = false)
    {
        if (_subs == null) return;
        // The main form does not exist yet at AfterLoad; project events are a good second chance.
        if (!_mainFormRegistered) RegisterMainFormHandler();
        try
        {
            if (appCom == null)
            {
                using var own = SystemExtensionFactory.GetApplication();
                if (own.IsNull) return;
                BindActiveProject(own, force);
                return;
            }
            using var projectCom = appCom.GetActiveProject();
            if (projectCom.IsNull) { _subs.Unbind(); return; }
            _subs.Bind(projectCom, force);
        }
        catch (Exception ex) { Log.Error("binding the active project failed", ex); }
    }

    public static void UnbindProject()
    {
        try { _subs?.Unbind(); }
        catch (Exception ex) { Log.Error("unbinding the project failed", ex); }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (!_started) return;
            _started = false;
        }
        Log.Info("stopping");
        _retryTimer?.Dispose(); _retryTimer = null;
        _pollTimer?.Dispose(); _pollTimer = null;
        try { Ui.PulseWindowHost.Close(); } catch { }

        try { _subs?.Unbind(); } catch (Exception ex) { Log.Error("unbind on stop", ex); }

        try
        {
            using var appCom = SystemExtensionFactory.GetApplication();
            if (!appCom.IsNull)
            {
                if (_mainFormRegistered)
                {
                    try
                    {
                        using var mainFormCom = appCom.MainForm();
                        if (!mainFormCom.IsNull) mainFormCom.Invoke(f => f.UnregisterHandler(MainFormHandler.Ident, out _));
                    }
                    catch (Exception ex) { Log.Warn($"unregister main form handler: {ex.Message}"); }
                }
                if (_appRegistered) appCom.Invoke(a => a.UnregisterHandler(AppHandlerIdent, out _));
            }
        }
        catch (Exception ex) { Log.Warn($"unregister application handler: {ex.Message}"); }
        _appRegistered = false;
        _mainFormRegistered = false;

        try
        {
            _dispatcher?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            _dispatcher?.Dispose();
        }
        catch (Exception ex) { Log.Error("dispatcher stop", ex); }

        _dispatcher = null;
        _subs = null;
        _appHandler = null;
        _mainFormHandler = null;
        _sim = null;
        _simSession = null;
        _tailer = null;
        _lastPoll = null;
        _calcStarted.Clear();
        lock (Gate)
        {
            _settings?.Dispose(); _settings = null;
            _rules?.Dispose(); _rules = null;
        }
        Log.Info("stopped");
        Log.Flush(TimeSpan.FromSeconds(1));
    }

    /// <summary>Event list for RegisterHandler. Empty by default: ENCY then calls every handler interface the object implements.</summary>
    public static ListString EventList(params Type[] handlerInterfaces)
    {
        var list = new ListString();
        var mode = Settings.Current.Diagnostics.HandlerEventListMode?.Trim().ToLowerInvariant() ?? "empty";
        switch (mode)
        {
            case "guids":
                foreach (var t in handlerInterfaces) list.Add(t.GUID.ToString("B"));
                break;
            case "names":
                foreach (var t in handlerInterfaces) list.Add(t.Name);
                break;
        }
        return list;
    }

    public static void Throw(TResultStatus st, string what)
    {
        if (st.Code == TResultStatusCode.rsError) throw new Exception($"{what} failed: {st.Description}");
    }

    /// <summary>Reads the active project's tree. Used from the dispatcher thread at batch close.</summary>
    public static TreeSnapshot? TrySnapshot()
    {
        try
        {
            using var appCom = SystemExtensionFactory.GetApplication();
            if (appCom.IsNull) return null;
            using var projectCom = appCom.GetActiveProject();
            if (projectCom.IsNull) return null;
            return TreeReader.Snapshot(projectCom);
        }
        catch (Exception ex)
        {
            Log.Warn($"tree snapshot failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Every 500 ms: one work-mode read. Every 2 s (0.5 s while simulating): a pass over the operation
    /// tree. Flag changes become completion events (deduplicated against real ones by the dispatcher)
    /// and feed the simulation detector.
    /// </summary>
    private static void Poll(object? _)
    {
        if (!_started || !_appRegistered || _dispatcher == null || _sim == null) return;
        if (Interlocked.Exchange(ref _pollBusy, 1) == 1) return;
        try
        {
            var diag = Settings.Current.Diagnostics;
            var now = DateTimeOffset.UtcNow;

            var tailing = diag.TailEncyLog && _tailer?.Path != null;
            if (tailing) TailEncyLog(now);

            // With ENCY's log as the event source, the tree poll is only a fallback and is skipped:
            // no periodic COM traffic competes with the user's work.
            if (tailing || (!diag.PollForCompletion && !diag.SimulationProbe)) return;

            using var appCom = SystemExtensionFactory.GetApplication();
            if (appCom.IsNull) return;
            var mode = appCom.MainWorkMode();
            if (mode != _lastMode)
            {
                Log.Debug($"work mode: {mode}");
                _lastMode = mode;
            }
            var simulating = mode == TMainWorkMode.mwmSimulating;

            _pollTick++;
            if (!simulating && _pollTick % 4 != 0) return;

            using var projectCom = appCom.GetActiveProject();
            if (projectCom.IsNull) { _lastPoll = null; return; }
            var snap = TreeReader.Snapshot(projectCom);

            var calcChanged = false;
            var prev = _lastPoll;
            if (prev != null && string.Equals(prev.ProjectId, snap.ProjectId, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var n in snap.Nodes)
                {
                    var p = prev.Get(n.Id);
                    if (p == null || n.IsGroup) continue;
                    if (p.Calculated == n.Calculated && p.HasToolpath == n.HasToolpath && p.Simulated == n.Simulated && p.IsError == n.IsError)
                        continue;

                    Log.Debug($"flags '{n.FullName}': calculated {p.Calculated}->{n.Calculated}, toolpath {p.HasToolpath}->{n.HasToolpath}, simulated {p.Simulated}->{n.Simulated}, error {p.IsError}->{n.IsError}");

                    if (p.Calculated != n.Calculated || p.HasToolpath != n.HasToolpath) calcChanged = true;
                    if (diag.PollForCompletion && n.Enabled && !p.Calculated && n.Calculated)
                    {
                        _dispatcher.Post(new OperationCalculatedEvent(snap.ProjectId, snap.ProjectName, n.Id, n.Name, n.FullName,
                            false, n.Calculated, n.HasToolpath, now) { Source = "poll" });
                    }
                }
            }
            _lastPoll = snap;

            // With the ENCY log available, simulations are taken from it; the flag detector would duplicate them.
            if (diag.SimulationProbe && !tailing)
            {
                var leaves = snap.AllLeaves().ToList();
                var simulated = leaves.Count(l => l.Simulated);
                var errors = leaves.Count(l => l.Simulated && l.IsError);
                var r = _sim.Sample(simulating, simulated, leaves.Count, errors, snap.ProjectName, now, calcChanged);
                if (r != null) _dispatcher.Post(r);
                else if (_sim.LastSkipReason != null) Log.Info($"simulation detector: {_sim.LastSkipReason}");
            }
        }
        catch (Exception ex) { Log.Debug($"poll: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _pollBusy, 0); }
    }

    /// <summary>Turns new ENCY log records into batch, operation and simulation signals.</summary>
    private static void TailEncyLog(DateTimeOffset now)
    {
        if (_tailer == null || _dispatcher == null || _simSession == null) return;
        IReadOnlyList<LogSignal> signals;
        try { signals = _tailer.ReadNew(); }
        catch (Exception ex)
        {
            if (++_tailFailures == 1) Log.Warn($"ENCY log tail failed: {ex.Message}");
            return;
        }

        foreach (var s in signals)
        {
            switch (s.Kind)
            {
                case LogSignalKind.BatchStarted:
                    Log.Debug("ENCY log: toolpath generation started");
                    _lastCalcActivity = now;
                    _dispatcher.Post(new ProgressEvent("log:Generating toolpath started", 0, now));
                    break;

                case LogSignalKind.CalcStarted:
                    Log.Debug($"ENCY log: calculation started '{s.Name}'");
                    _calcStarted[s.Name] = s.At;
                    _lastCalcActivity = now;
                    _dispatcher.Post(new ProgressEvent($"log:calculating {s.Name}", 0, now));
                    break;

                case LogSignalKind.CalcCompleted:
                case LogSignalKind.CalcFailed:
                    _calcStarted.Remove(s.Name);
                    _lastCalcActivity = now;
                    PostFromLog(s, now);
                    break;

                case LogSignalKind.ToolpathReset:
                    Log.Debug($"ENCY log: toolpath reset '{s.Name}'");
                    break;

                case LogSignalKind.SimulationStarted:
                case LogSignalKind.SimulationFinished:
                    _simSession.OnSignal(s);
                    break;

                case LogSignalKind.NcGenerationStarted:
                case LogSignalKind.NcGenerationFinished:
                    Log.Debug($"ENCY log: {s.Kind}");
                    break;
            }
        }

        // While ENCY is between "Start the operation calculation" and its completion line, keep the batch open.
        if (_calcStarted.Count > 0)
            _dispatcher.Post(new ProgressEvent("log:calculating", 0, now));

        var run = _simSession.Tick(now);
        if (run != null)
        {
            var (startedAt, duration, finished, names) = run.Value;
            var grace = TimeSpan.FromSeconds(Math.Max(0, Settings.Current.Diagnostics.AutoSimulationGraceSec));
            // Project loading starts before the AfterLoadProject event, so compare in both directions.
            var sinceLoad = (startedAt - _lastProjectLoad).Duration();
            var sinceCalc = startedAt - _lastCalcActivity;
            var automatic = sinceLoad < grace || (sinceCalc >= TimeSpan.Zero && sinceCalc < grace);
            var project = _lastPoll?.ProjectName ?? "";
            if (finished == 0)
            {
                Log.Debug("simulation run with no finished operation ignored");
            }
            else if (automatic)
            {
                Log.Info($"simulation of {finished} operations ({Templates.Duration(duration)}) followed a project load or calculation by less than {grace.TotalSeconds:F0} s; treated as ENCY's automatic stock update");
            }
            else
            {
                var snap = _lastPoll ?? TrySnapshot();
                if (snap != null) _lastPoll = snap;
                var leaves = snap?.AllLeaves().ToList() ?? new List<OpNode>();
                var ops = new List<(string Id, string Name)>();
                foreach (var name in names)
                {
                    var node = leaves.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                    ops.Add(node != null ? (node.Id, node.Name) : ("name:" + name, name));
                }
                var errors = leaves.Count(l => l.IsError && names.Contains(l.Name, StringComparer.OrdinalIgnoreCase));
                var matched = ops.Count(o => !o.Id.StartsWith("name:", StringComparison.Ordinal));
                var whole = leaves.Count > 0 && matched >= leaves.Count;
                _dispatcher.Post(new SimulationRunEvent(snap?.ProjectId ?? "", project, duration, ops, leaves.Count, errors, whole, now));
            }
        }
    }

    /// <summary>Resolve the operation named in a log line to its id and flags, then post a completion event.</summary>
    private static void PostFromLog(LogSignal s, DateTimeOffset now)
    {
        if (_dispatcher == null) return;
        var succeeded = s.Kind == LogSignalKind.CalcCompleted;
        TreeSnapshot? snap = null;
        try
        {
            snap = TrySnapshot();
            if (snap != null) _lastPoll = snap;
        }
        catch { /* fall back to the previous snapshot */ }
        snap ??= _lastPoll;

        var node = snap?.Nodes.FirstOrDefault(n => !n.IsGroup && string.Equals(n.Name, s.Name, StringComparison.OrdinalIgnoreCase))
                   ?? snap?.Nodes.FirstOrDefault(n => !n.IsGroup && n.FullName.EndsWith("/" + s.Name, StringComparison.OrdinalIgnoreCase));

        // "Body toolpath length: full - 0, ..." means the calculation ended without a toolpath.
        var hasToolpath = succeeded;
        var lengthMatch = System.Text.RegularExpressions.Regex.Match(s.Detail ?? "", @"full\s*-\s*([0-9]+(?:[.,][0-9]+)?)");
        if (lengthMatch.Success &&
            double.TryParse(lengthMatch.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var length) && length <= 0)
            hasToolpath = false;

        var evt = node != null
            ? new OperationCalculatedEvent(snap!.ProjectId, snap.ProjectName, node.Id, node.Name, node.FullName, false,
                succeeded, hasToolpath, now) { Source = "log" }
            : new OperationCalculatedEvent(snap?.ProjectId ?? "", snap?.ProjectName ?? "", "name:" + s.Name, s.Name, s.Name, false,
                succeeded, hasToolpath, now) { Source = "log" };

        if (node == null) Log.Debug($"ENCY log: operation '{s.Name}' not found in the tree; reporting by name");
        if (!string.IsNullOrEmpty(s.Detail)) Log.Debug($"ENCY log detail: {s.Detail}");
        _dispatcher.Post(evt);
    }
}
