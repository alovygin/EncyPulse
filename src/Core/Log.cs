using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace EncyPulse.Core;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// Asynchronous file logger: callers only enqueue a line (microseconds, no disk access on ENCY's
/// thread); a background thread writes and rotates the file. Never throws.
/// </summary>
public sealed class Log : IDisposable
{
    private const long MaxBytes = 5_000_000;
    private const int MaxQueued = 20_000;
    private readonly string _path;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Thread _writer;
    private int _dropped;

    public LogLevel MinLevel { get; set; } = LogLevel.Info;

    public string Path => _path;

    public Log(string path)
    {
        _path = path;
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); } catch { /* best effort */ }
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "ENCY Pulse log" };
        _writer.Start();
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message, Exception? ex = null) =>
        Write(LogLevel.Error, ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private void Write(LogLevel level, string message)
    {
        if (level < MinLevel) return;
        if (_queue.IsAddingCompleted) return;
        if (_queue.Count > MaxQueued) { Interlocked.Increment(ref _dropped); return; }
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [t{Environment.CurrentManagedThreadId,3}] {message}";
        try { _queue.TryAdd(line); } catch { /* shutting down */ }
    }

    private void WriterLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = _path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                var prefix = dropped > 0 ? $"[log] {dropped} lines dropped under load{Environment.NewLine}" : "";
                File.AppendAllText(_path, prefix + line + Environment.NewLine);
            }
            catch { /* logging must never break the host */ }
        }
    }

    /// <summary>Wait (bounded) until queued lines are on disk. Used at shutdown.</summary>
    public void Flush(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (_queue.Count > 0 && DateTime.UtcNow < until) Thread.Sleep(10);
    }

    public void Dispose()
    {
        try
        {
            _queue.CompleteAdding();
            _writer.Join(TimeSpan.FromSeconds(1));
        }
        catch { }
    }
}
