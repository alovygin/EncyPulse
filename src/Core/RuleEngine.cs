using System;
using System.Linq;
using System.Collections.Generic;
namespace EncyPulse.Core;

/// <summary>
/// Turns events and closed batches into notifications. Two scenarios, deliberately kept apart:
/// "Entire project" (settings switches) and "Selected operations" (per-operation rules).
/// </summary>
public sealed class RuleEngine
{
    private readonly Func<Settings> _settings;
    private readonly Func<NotifyRules> _rules;

    public RuleEngine(Func<Settings> settings, Func<NotifyRules> rules)
    {
        _settings = settings;
        _rules = rules;
    }

    /// <summary>Selected operations, calculation: fires immediately when that operation finishes.</summary>
    public IReadOnlyList<Notification> ForOperationEvent(OperationCalculatedEvent e, TimeSpan? approxDuration)
    {
        if (e.IsGroup) return Array.Empty<Notification>();
        var rule = _rules().For(e.ProjectId, e.OperationId);
        if (rule == null || !rule.Calculation) return Array.Empty<Notification>();

        var s = _settings();
        var (title, body) = e.Succeeded ? Templates.OperationCompleted(e, approxDuration) : Templates.OperationFailed(e);
        return new[]
        {
            new Notification
            {
                Kind = e.Succeeded ? NotificationKind.OperationCompleted : NotificationKind.OperationFailed,
                Title = title, Body = body,
                ProjectName = e.ProjectName, Subject = e.FullName,
                Priority = e.Succeeded ? 0 : 1,
                Channels = ResolveChannels(s, rule.Channels),
                Stats = { ["operationId"] = e.OperationId },
            }
        };
    }

    /// <summary>Entire project, calculation: evaluated once per batch. Failures are reported when enabled.</summary>
    public IReadOnlyList<Notification> ForBatch(Batch b, TreeSnapshot? snap)
    {
        if (b.Events.Count == 0) return Array.Empty<Notification>();

        var s = _settings();
        var d = s.Defaults;
        var channels = ResolveChannels(s, null);
        var projectName = b.ProjectName ?? snap?.ProjectName ?? "";
        var result = new List<Notification>();

        var touched = b.Events.Where(e => !e.IsGroup)
                              .GroupBy(e => e.OperationId, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var failed = touched.Values.Where(e => !e.Succeeded).Select(e => e.Name).Distinct().ToList();

        if (d.NotifyProjectCompleted && snap != null)
        {
            var leaves = snap.AllLeaves().ToList();
            var allDone = leaves.Count > 0 && leaves.All(l => l.Calculated) && leaves.Any(l => touched.ContainsKey(l.Id));
            var longEnough = d.IgnoreShorterThanSec <= 0 || b.Duration.TotalSeconds >= d.IgnoreShorterThanSec;
            if (allDone && longEnough)
            {
                var (title, body) = Templates.ProjectCompleted(projectName, leaves.Count, b.Duration, failed);
                result.Add(new Notification
                {
                    Kind = NotificationKind.ProjectCompleted, Title = title, Body = body,
                    ProjectName = projectName, Subject = projectName, Priority = 1, Channels = channels,
                    Stats = { ["operations"] = leaves.Count.ToString(), ["failed"] = failed.Count.ToString(), ["durationSec"] = ((int)b.Duration.TotalSeconds).ToString() },
                });
            }
        }

        if (d.NotifyFailures && failed.Count > 0 && result.Count == 0)
        {
            var (title, body) = Templates.BatchFailed(projectName, failed, touched.Count);
            result.Add(new Notification
            {
                Kind = NotificationKind.BatchFailed, Title = title, Body = body,
                ProjectName = projectName, Subject = projectName, Priority = 1, Channels = channels,
                Stats = { ["failed"] = failed.Count.ToString() },
            });
        }

        return result;
    }

    /// <summary>
    /// Simulation: "Project simulation completed." when the run covered the whole project and the
    /// switch is on; otherwise one "Simulation completed for 'X'." per selected operation in the run.
    /// </summary>
    public IReadOnlyList<Notification> ForSimulationRun(SimulationRunEvent run)
    {
        var s = _settings();
        var d = s.Defaults;
        var result = new List<Notification>();

        if (run.CoversWholeProject && d.NotifySimulationCompleted)
        {
            if (d.IgnoreShorterThanSec > 0 && run.Duration.TotalSeconds < d.IgnoreShorterThanSec) return result;
            var (title, body) = Templates.ProjectSimulated(run.ProjectName, run.TotalOperations, run.Duration, run.ErrorCount);
            result.Add(new Notification
            {
                Kind = NotificationKind.SimulationCompleted, Title = title, Body = body,
                ProjectName = run.ProjectName, Subject = run.ProjectName, Priority = run.ErrorCount > 0 ? 1 : 0,
                Channels = ResolveChannels(s, null),
                Stats = { ["operations"] = run.TotalOperations.ToString(), ["errors"] = run.ErrorCount.ToString(), ["durationSec"] = ((int)run.Duration.TotalSeconds).ToString() },
            });
            return result;
        }

        var rules = _rules();
        foreach (var (id, name) in run.Operations)
        {
            var rule = rules.For(run.ProjectId, id);
            if (rule == null || !rule.Simulation) continue;
            var (title, body) = Templates.OperationSimulated(name, run.ProjectName, run.Duration);
            result.Add(new Notification
            {
                Kind = NotificationKind.OperationSimulated, Title = title, Body = body,
                ProjectName = run.ProjectName, Subject = name, Channels = ResolveChannels(s, rule.Channels),
                Stats = { ["operationId"] = id },
            });
        }
        return result;
    }

    public Notification TestNotification()
    {
        var s = _settings();
        var (title, body) = Templates.Test(Environment.MachineName);
        return new Notification { Kind = NotificationKind.Test, Title = title, Body = body, Channels = ResolveChannels(s, null) };
    }

    private static List<string> ResolveChannels(Settings s, List<string>? override_)
    {
        var enabled = s.EnabledChannels().ToList();
        if (override_ == null || override_.Count == 0) return enabled;
        return override_.Where(c => enabled.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
    }
}
