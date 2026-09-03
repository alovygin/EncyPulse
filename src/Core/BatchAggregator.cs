using System;
using System.Collections.Generic;
namespace EncyPulse.Core;

/// <summary>
/// Groups per-operation events into batches. A batch opens on the first activity after idle and
/// closes when neither an operation event nor a progress update arrived for the quiet window.
/// Not thread-safe: drive it from one thread (the dispatcher).
/// </summary>
public sealed class BatchAggregator
{
    private readonly Func<TimeSpan> _quietWindow;
    private Batch? _open;
    private DateTimeOffset _lastActivity;

    public event Action<Batch>? BatchClosed;

    public BatchAggregator(Func<TimeSpan> quietWindow) => _quietWindow = quietWindow;

    public bool IsOpen => _open != null;
    public Batch? Open => _open;

    /// <summary>Time since the previous operation event in this batch (or since batch start). Null when no batch is open.</summary>
    public TimeSpan? ElapsedSinceLastOperation(DateTimeOffset now)
    {
        if (_open == null) return null;
        var last = _open.Events.Count > 0 ? _open.Events[^1].At : _open.StartedAt;
        return now - last;
    }

    public void OnOperation(OperationCalculatedEvent e)
    {
        EnsureOpen(e.At);
        _open!.ProjectId ??= e.ProjectId;
        _open.ProjectName ??= e.ProjectName;
        _open.Events.Add(e);
        _lastActivity = e.At;
    }

    public void OnProgress(ProgressEvent p)
    {
        EnsureOpen(p.At);
        if (!string.IsNullOrWhiteSpace(p.Caption) &&
            (_open!.Captions.Count == 0 || !string.Equals(_open.Captions[^1], p.Caption, StringComparison.Ordinal)))
            _open.Captions.Add(p.Caption);
        _lastActivity = p.At;
    }

    /// <summary>Call periodically (every ~250 ms).</summary>
    public void Tick(DateTimeOffset now)
    {
        if (_open != null && now - _lastActivity >= _quietWindow()) Close(now);
    }

    /// <summary>Close whatever is open right now (shutdown).</summary>
    public void ForceClose(DateTimeOffset now)
    {
        if (_open != null) Close(now);
    }

    private void EnsureOpen(DateTimeOffset at)
    {
        if (_open != null) return;
        _open = new Batch { StartedAt = at };
        _lastActivity = at;
    }

    private void Close(DateTimeOffset now)
    {
        var b = _open!;
        _open = null;
        b.EndedAt = _lastActivity > b.StartedAt ? _lastActivity : b.StartedAt;
        BatchClosed?.Invoke(b);
    }
}
