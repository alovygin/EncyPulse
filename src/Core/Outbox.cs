using System;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;

namespace EncyPulse.Core;

public sealed class OutboxItem
{
    public Notification Notification { get; set; } = new();
    public List<string> PendingChannels { get; set; } = new();
    public int Attempts { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }
}

/// <summary>Disk-backed queue: one JSON file per notification, retried with backoff until every channel succeeded.</summary>
public sealed class Outbox
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private const int MaxAttempts = 10;
    private readonly string _dir;
    private readonly Log _log;
    private readonly object _gate = new();

    public Outbox(string dir, Log log)
    {
        _dir = dir;
        _log = log;
        try { Directory.CreateDirectory(dir); } catch (Exception ex) { _log.Error($"cannot create outbox {dir}", ex); }
    }

    public string Directory_ => _dir;

    public void Enqueue(Notification n, DateTimeOffset notBefore)
    {
        var item = new OutboxItem { Notification = n, PendingChannels = n.Channels.Distinct().ToList(), NotBefore = notBefore };
        Write(item);
        _log.Info($"queued {n.Kind} '{n.Title}' for [{string.Join(",", item.PendingChannels)}]" +
                  (notBefore > DateTimeOffset.UtcNow ? $" not before {notBefore.ToLocalTime():HH:mm}" : ""));
    }

    public int PendingCount()
    {
        try { return Directory.GetFiles(_dir, "*.json").Length; } catch { return 0; }
    }

    public static TimeSpan Backoff(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(10),
        4 => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromHours(1),
    };

    /// <summary>Sends everything that is due. Safe to call often; does nothing when the folder is empty.</summary>
    public async Task ProcessAsync(IReadOnlyDictionary<string, INotificationSender> senders, CancellationToken ct)
    {
        string[] files;
        try { files = Directory.GetFiles(_dir, "*.json"); } catch { return; }
        var now = DateTimeOffset.UtcNow;

        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            if (ct.IsCancellationRequested) return;
            OutboxItem? item;
            try { item = JsonSerializer.Deserialize<OutboxItem>(File.ReadAllText(file), JsonFileStore<Settings>.JsonOptions); }
            catch (Exception ex) { _log.Warn($"unreadable outbox item {Path.GetFileName(file)}: {ex.Message}; removing"); TryDelete(file); continue; }
            if (item == null) { TryDelete(file); continue; }
            if (item.NotBefore > now) continue;
            if (now - item.CreatedAt > MaxAge || item.Attempts >= MaxAttempts)
            {
                _log.Warn($"giving up on '{item.Notification.Title}' after {item.Attempts} attempts ({string.Join(",", item.PendingChannels)} still pending)");
                TryDelete(file);
                continue;
            }

            var failed = false;
            foreach (var channel in item.PendingChannels.ToList())
            {
                if (ct.IsCancellationRequested) return;
                if (!senders.TryGetValue(channel, out var sender))
                {
                    _log.Warn($"channel '{channel}' is not configured any more; dropping it for '{item.Notification.Title}'");
                    item.PendingChannels.Remove(channel);
                    continue;
                }
                try
                {
                    await sender.SendAsync(item.Notification, ct).ConfigureAwait(false);
                    item.PendingChannels.Remove(channel);
                    _log.Info($"sent '{item.Notification.Title}' via {channel}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    failed = true;
                    item.LastError = $"{channel}: {ex.Message}";
                    _log.Warn($"send via {channel} failed (attempt {item.Attempts + 1}): {ex.Message}");
                }
            }

            if (item.PendingChannels.Count == 0) { TryDelete(file); continue; }
            if (failed)
            {
                item.Attempts++;
                item.NotBefore = now + Backoff(item.Attempts);
            }
            Write(item, file);
        }
    }

    private void Write(OutboxItem item, string? path = null)
    {
        lock (_gate)
        {
            try
            {
                path ??= Path.Combine(_dir, $"{item.CreatedAt.UtcDateTime:yyyyMMddHHmmssfff}-{item.Notification.Id}.json");
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(item, JsonFileStore<Settings>.JsonOptions));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) { _log.Error("outbox write failed", ex); }
        }
    }

    private void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* next pass */ }
    }
}
