using System;
using System.Linq;
using System.Collections.Generic;
namespace EncyPulse.Core;

/// <summary>One node of the technology tree, read once and kept as plain data.</summary>
public sealed record OpNode(
    string Id,
    string Name,
    string FullName,
    string? ParentId,
    bool IsGroup,
    bool Enabled,
    bool Calculated,
    bool HasToolpath,
    bool IsError,
    /// <summary>The simulator's own flag (ICamApiTechOperation.Simulated), not the machining-result flag.</summary>
    bool Simulated);

/// <summary>A point-in-time copy of the operation tree. No COM references inside.</summary>
public sealed class TreeSnapshot
{
    public string ProjectId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public DateTimeOffset TakenAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<OpNode> Nodes { get; init; } = Array.Empty<OpNode>();

    private Dictionary<string, OpNode>? _byId;
    private Dictionary<string, List<OpNode>>? _children;

    private Dictionary<string, OpNode> ById =>
        _byId ??= Nodes.GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, List<OpNode>> ChildrenMap
    {
        get
        {
            if (_children != null) return _children;
            var map = new Dictionary<string, List<OpNode>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in Nodes)
            {
                if (n.ParentId == null) continue;
                if (!map.TryGetValue(n.ParentId, out var list)) map[n.ParentId] = list = new List<OpNode>();
                list.Add(n);
            }
            return _children = map;
        }
    }

    public OpNode? Get(string id) => ById.TryGetValue(id, out var n) ? n : null;

    public IEnumerable<OpNode> Children(string id) =>
        ChildrenMap.TryGetValue(id, out var list) ? list : Enumerable.Empty<OpNode>();

    /// <summary>Enabled, non-group descendants of a node (the operations that actually get toolpaths).</summary>
    public IEnumerable<OpNode> Leaves(string id)
    {
        var stack = new Stack<OpNode>(Children(id));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!seen.Add(n.Id)) continue;
            if (!n.Enabled) continue;
            if (n.IsGroup) { foreach (var c in Children(n.Id)) stack.Push(c); continue; }
            yield return n;
        }
    }

    /// <summary>Every enabled, non-group operation in the project.</summary>
    public IEnumerable<OpNode> AllLeaves() => Nodes.Where(n => !n.IsGroup && n.Enabled);

    public IEnumerable<string> AncestorIds(string id)
    {
        var cur = Get(id);
        var guard = 0;
        while (cur?.ParentId != null && guard++ < 256)
        {
            yield return cur.ParentId;
            cur = Get(cur.ParentId);
        }
    }

    /// <summary>Returns a copy where the outcomes carried by events override the stored flags.</summary>
    public TreeSnapshot WithEvents(IEnumerable<OperationCalculatedEvent> events)
    {
        var overrides = events.GroupBy(e => e.OperationId, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        if (overrides.Count == 0) return this;
        var nodes = Nodes.Select(n => overrides.TryGetValue(n.Id, out var e)
                ? n with { Calculated = e.Calculated, HasToolpath = e.HasToolpath }
                : n).ToList();
        return new TreeSnapshot { ProjectId = ProjectId, ProjectName = ProjectName, ProjectPath = ProjectPath, TakenAt = TakenAt, Nodes = nodes };
    }
}

/// <summary>Raised by ENCY when one operation's toolpath calculation finished (successfully or not).</summary>
public sealed record OperationCalculatedEvent(
    string ProjectId,
    string ProjectName,
    string OperationId,
    string Name,
    string FullName,
    bool IsGroup,
    bool Calculated,
    bool HasToolpath,
    DateTimeOffset At)
{
    public bool Succeeded => Calculated && HasToolpath;

    /// <summary>"event" when ENCY raised ToolpathCalculated, "poll" when the tree poll saw the flag change.</summary>
    public string Source { get; init; } = "event";

    /// <summary>Optional tree copy taken on ENCY's thread together with the event (fallback mode).</summary>
    public TreeSnapshot? Snapshot { get; init; }
}

/// <summary>ENCY's process-state progress hook: caption and percent of whatever is running.</summary>
public sealed record ProgressEvent(string Caption, int Percent, DateTimeOffset At);

/// <summary>Result of the simulation detector: one fast simulation run has ended.</summary>
public sealed record SimulationResult(
    string ProjectName,
    TimeSpan Duration,
    int SimulatedCount,
    int TotalOperations,
    int ErrorCount,
    DateTimeOffset At);

/// <summary>
/// One simulation run seen in ENCY's log: which operations were simulated (resolved to tree ids where
/// possible) and whether the run covered the whole project.
/// </summary>
public sealed record SimulationRunEvent(
    string ProjectId,
    string ProjectName,
    TimeSpan Duration,
    IReadOnlyList<(string Id, string Name)> Operations,
    int TotalOperations,
    int ErrorCount,
    bool CoversWholeProject,
    DateTimeOffset At);

/// <summary>A burst of calculation activity, bounded by a quiet window.</summary>
public sealed class Batch
{
    public string? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; set; }
    public List<OperationCalculatedEvent> Events { get; } = new();
    public List<string> Captions { get; } = new();
    public TimeSpan Duration => EndedAt - StartedAt;
}

public enum NotificationKind
{
    OperationCompleted,     // Operation 'X' calculation completed.
    OperationFailed,        // Operation 'X' calculation failed.
    ProjectCompleted,       // Project calculation completed.
    BatchFailed,            // Calculation stopped with errors.
    SimulationCompleted,    // Project simulation completed.
    OperationSimulated,     // Simulation completed for 'X'.
    Test
}

/// <summary>A rendered message ready for the outbox. Channels are the keys of the senders to use.</summary>
public sealed class Notification
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public NotificationKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Subject { get; init; } = "";
    public int Priority { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<string> Channels { get; init; } = new();
    public Dictionary<string, string> Stats { get; init; } = new();
}
