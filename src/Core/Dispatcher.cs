using System;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Channels;

namespace EncyPulse.Core;

/// <summary>
/// The single background worker. Everything that is not a five-microsecond property read happens
/// here: batching, rule evaluation, suppression, outbox processing, network.
/// </summary>
public sealed class Dispatcher : IDisposable
{
    private sealed record RefreshSendersSignal;

    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly BatchAggregator _agg;
    private readonly RuleEngine _rules;
    private readonly Suppression _suppression;
    private readonly Outbox _outbox;
    private readonly Func<Settings> _settings;
    private readonly Log _log;
    private Dictionary<string, INotificationSender> _senders;
    private Task? _loop;
    private TreeSnapshot? _lastSnapshot;
    private DateTimeOffset _lastOutboxRun;
    private string? _lastCaption;
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(15);
    private readonly Dictionary<string, (DateTimeOffset At, bool Succeeded, string Source)> _recent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the current tree at batch close. Runs on the dispatcher thread.</summary>
    public Func<TreeSnapshot?>? SnapshotProvider { get; set; }

    public BatchAggregator Aggregator => _agg;
    public Outbox Outbox => _outbox;

    public Dispatcher(Func<Settings> settings, Func<NotifyRules> rules, Outbox outbox, Func<double> idleSeconds, Log log)
    {
        _settings = settings;
        _log = log;
        _outbox = outbox;
        _rules = new RuleEngine(settings, rules);
        _suppression = new Suppression(idleSeconds, settings);
        _agg = new BatchAggregator(() => TimeSpan.FromMilliseconds(Math.Max(250, settings().Defaults.QuietWindowMs)));
        _agg.BatchClosed += OnBatchClosed;
        _senders = SenderFactory.Build(settings(), log);
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    public void Post(object item) => _queue.Writer.TryWrite(item);

    public void RefreshSenders() => Post(new RefreshSendersSignal());

    /// <summary>Queues a test message on every enabled channel and returns its id (the outbox file name contains it).</summary>
    public string SendTest()
    {
        var n = _rules.TestNotification();
        Post(n);
        return n.Id;
    }

    private async Task LoopAsync()
    {
        var reader = _queue.Reader;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var wait = reader.WaitToReadAsync(_cts.Token).AsTask();
                await Task.WhenAny(wait, Task.Delay(250, _cts.Token)).ConfigureAwait(false);
                while (reader.TryRead(out var item)) Handle(item);

                var now = DateTimeOffset.UtcNow;
                _agg.Tick(now);
                if (now - _lastOutboxRun >= TimeSpan.FromSeconds(2))
                {
                    _lastOutboxRun = now;
                    await _outbox.ProcessAsync(_senders, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Error("dispatcher loop", ex); }
        }
    }

    private void Handle(object item)
    {
        switch (item)
        {
            case OperationCalculatedEvent e:
                if (_recent.TryGetValue(e.OperationId, out var prev) && prev.Succeeded == e.Succeeded &&
                    e.At - prev.At < DuplicateWindow)
                {
                    _log.Debug($"duplicate {e.Source} report for '{e.FullName}' ignored (already seen from {prev.Source})");
                    break;
                }
                if (_recent.Count > 5000) _recent.Clear();
                _recent[e.OperationId] = (e.At, e.Succeeded, e.Source);

                if (e.Snapshot != null) _lastSnapshot = e.Snapshot;
                var approx = _agg.ElapsedSinceLastOperation(e.At);
                _agg.OnOperation(e);
                _log.Info($"operation {(e.Succeeded ? "completed" : "FAILED")} [{e.Source}]: '{e.FullName}' (calculated={e.Calculated}, toolpath={e.HasToolpath})");
                Route(_rules.ForOperationEvent(e, approx));
                break;

            case ProgressEvent p:
                if (!string.Equals(p.Caption, _lastCaption, StringComparison.Ordinal))
                {
                    _lastCaption = p.Caption;
                    _log.Debug($"progress caption: '{p.Caption}' {p.Percent}%");
                }
                _agg.OnProgress(p);
                break;

            case SimulationResult r:
                // Flag-based detector (no ENCY log available): only the whole-project case can be told.
                Handle(new SimulationRunEvent(_lastSnapshot?.ProjectId ?? "", r.ProjectName, r.Duration,
                    Array.Empty<(string, string)>(), r.TotalOperations, r.ErrorCount,
                    r.SimulatedCount >= r.TotalOperations && r.TotalOperations > 0, r.At));
                break;

            case SimulationRunEvent run:
                _log.Info($"simulation run: {run.Operations.Count} operations, whole project={run.CoversWholeProject}, {Templates.Duration(run.Duration)}, errors={run.ErrorCount}");
                var simNotifications = _rules.ForSimulationRun(run);
                if (simNotifications.Count == 0)
                    _log.Info(run.CoversWholeProject
                        ? "no notification: 'Project simulation completed' is switched off or the run was too short, and no selected operation asks for simulation"
                        : "no notification: no selected operation with 'Simulation' was in this run");
                Route(simNotifications);
                break;

            case Notification n:
                Route(new[] { n });
                break;

            case RefreshSendersSignal:
                _senders = SenderFactory.Build(_settings(), _log);
                break;
        }
    }

    private void OnBatchClosed(Batch b)
    {
        try
        {
            TreeSnapshot? snap = null;
            if (b.Events.Count > 0)
            {
                snap = _settings().Diagnostics.BackgroundSnapshot ? (SnapshotProvider?.Invoke() ?? _lastSnapshot) : _lastSnapshot;
                snap = snap?.WithEvents(b.Events);
            }
            var completed = b.Events.Count(e => e.Succeeded);
            var failed = b.Events.Count(e => !e.Succeeded && !e.IsGroup);
            _log.Info($"batch closed: {completed} completed, {failed} failed, {Templates.Duration(b.Duration)}, captions: [{string.Join(" | ", b.Captions.Take(6))}]" +
                      (snap == null && b.Events.Count > 0 ? " (no tree snapshot available)" : ""));
            var notifications = _rules.ForBatch(b, snap);
            if (notifications.Count == 0 && b.Events.Count > 0)
            {
                var d = _settings().Defaults;
                var leaves = snap?.AllLeaves().ToList();
                var projectState = leaves == null ? "tree unknown" : $"{leaves.Count(l => l.Calculated)}/{leaves.Count} operations calculated";
                _log.Info($"no project notification for this batch ({Templates.Duration(b.Duration)}): {projectState}; 'Project calculation completed' is {(d.NotifyProjectCompleted ? "on" : "off")}" +
                          (d.IgnoreShorterThanSec > 0 ? $", runs under {d.IgnoreShorterThanSec} s are ignored" : ""));
            }
            Route(notifications);
        }
        catch (Exception ex) { _log.Error("batch evaluation failed", ex); }
    }

    private void Route(IEnumerable<Notification> notifications)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var n in notifications)
        {
            if (n.Channels.Count == 0)
            {
                _log.Info($"no channels enabled; not sending {n.Kind} '{n.Title}'");
                continue;
            }
            var d = _suppression.Decide(n, now);
            switch (d.Action)
            {
                case SuppressionAction.Drop:
                    _log.Info($"suppressed {n.Kind} '{n.Title}': {d.Reason}");
                    break;
                case SuppressionAction.Defer:
                    _log.Info($"deferred {n.Kind} '{n.Title}': {d.Reason}");
                    _outbox.Enqueue(n, d.Until ?? now);
                    break;
                default:
                    _outbox.Enqueue(n, now);
                    break;
            }
        }
    }

    /// <summary>Closes the open batch, gives the outbox one last chance, then stops the loop.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        try
        {
            _agg.ForceClose(DateTimeOffset.UtcNow);
            using var graceCts = new CancellationTokenSource(grace);
            await _outbox.ProcessAsync(_senders, graceCts.Token).ConfigureAwait(false);
        }
        catch { /* best effort */ }
        _cts.Cancel();
        try { if (_loop != null) await _loop.WaitAsync(grace).ConfigureAwait(false); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
