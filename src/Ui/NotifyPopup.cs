using System;
using System.Linq;
using System.Collections.Generic;
using CAMAPI.DotnetHelper;
using CAMAPI.Extensions;
using CAMAPI.Logger;
using CAMAPI.Project;
using CAMAPI.ResultStatus;
using CAMAPI.TechnologyForm;
using CAMAPI.UIDialogs.DotnetHelper;
using EncyPulse.Capture;
using EncyPulse.Core;

namespace EncyPulse.Ui;

/// <summary>
/// Right-click menu of an operation: the two "Selected operations" switches, directly in ENCY's tree.
/// On a group they apply to every operation inside it.
/// </summary>
public sealed class NotifyPopup : IExtension, IExtensionOperationPopup
{
    public IExtensionInfo? Info { get; set; }

    public void Build(IExtensionOperationPopupBuildContext context, out TResultStatus resultStatus)
    {
        resultStatus = default;
        try
        {
            var op = context.SelectedOperation;
            var project = context.ActiveProject;
            if (op == null || project == null) return;

            var isGroup = op.IsGroup;
            var rule = isGroup ? null : Runtime.Rules.Current.For(project.Id, op.Id);
            var calcOn = rule?.Calculation == true;
            var simOn = rule?.Simulation == true;
            var what = isGroup ? "operations in this group" : "this operation";

            context.OperationPopup.AddItem("EncyPulse.Calc",
                $"{(calcOn ? "✓ " : "")}ENCY Pulse: notify when {what} {(isGroup ? "are" : "is")} calculated",
                true, new ToggleHandler(ToggleHandler.Mode.Calculation), out resultStatus);
            if (resultStatus.Code == TResultStatusCode.rsError) return;

            context.OperationPopup.AddItem("EncyPulse.Sim",
                $"{(simOn ? "✓ " : "")}ENCY Pulse: notify when {what} {(isGroup ? "are" : "is")} simulated",
                true, new ToggleHandler(ToggleHandler.Mode.Simulation), out resultStatus);
        }
        catch (Exception e)
        {
            resultStatus.Code = TResultStatusCode.rsError;
            resultStatus.Description = e.Message;
        }
    }
}

internal sealed class ToggleHandler : ICamApiTechnologyFormOperationPopupItemOnClicked
{
    public enum Mode { Calculation, Simulation }

    private readonly Mode _mode;
    public ToggleHandler(Mode mode) => _mode = mode;

    public void OnItemClicked(IExtensionOperationPopupItemOnClickedContext context, out TResultStatus resultStatus)
    {
        resultStatus = default;
        try
        {
            var op = context.SelectedOperation ?? throw new Exception("No operation selected");
            var project = context.ActiveProject ?? throw new Exception("No active project");
            string projectId = project.Id, filePath = project.FilePath;
            string opId = op.Id, name = op.Name, fullName = op.FullName;
            var isGroup = op.IsGroup;

            // Targets: the operation itself, or every enabled operation under the group.
            var targets = new List<(string Id, string FullName)>();
            if (!isGroup) targets.Add((opId, fullName));
            else
            {
                using var projectCom = ComWrapper.Create(project);
                var snap = TreeReader.Snapshot(projectCom);
                targets.AddRange(snap.Leaves(opId).Select(l => (l.Id, l.FullName)));
            }
            if (targets.Count == 0)
            {
                UIDialogs.Notify(TLogEventType.leInfo, "ENCY Pulse", $"'{name}' contains no operations to notify about.");
                return;
            }

            // Toggle: if every target already has the flag, clear it; otherwise set it for all.
            var rules = Runtime.Rules.Current;
            var allOn = targets.All(t =>
            {
                var r = rules.For(projectId, t.Id);
                return r != null && (_mode == Mode.Calculation ? r.Calculation : r.Simulation);
            });
            var turnOn = !allOn;

            Runtime.Rules.Update(r =>
            {
                foreach (var t in targets)
                    r.SetOperation(projectId, filePath, t.Id, t.FullName,
                        calculation: _mode == Mode.Calculation ? turnOn : null,
                        simulation: _mode == Mode.Simulation ? turnOn : null);
            });

            var kind = _mode == Mode.Calculation ? "calculation" : "simulation";
            var subject = isGroup ? $"the {targets.Count} operations in '{name}'" : $"'{name}'";
            Runtime.Log.Info($"popup: {kind} notifications {(turnOn ? "on" : "off")} for {subject}");
            UIDialogs.Notify(TLogEventType.leInfo, "ENCY Pulse",
                turnOn ? $"You will be notified when the {kind} of {subject} completes."
                       : $"{kind[..1].ToUpperInvariant()}{kind[1..]} notifications for {subject} are off.");

            if (turnOn && !Runtime.Settings.Current.EnabledChannels().Any())
                UIDialogs.Notify(TLogEventType.leWarning, "ENCY Pulse", "No delivery channel is set up yet. Open Utilities → ENCY Pulse → Delivery.");
        }
        catch (Exception e)
        {
            try { Runtime.Log.Error("popup toggle", e); } catch { }
            resultStatus.Code = TResultStatusCode.rsError;
            resultStatus.Description = e.Message;
        }
    }
}
