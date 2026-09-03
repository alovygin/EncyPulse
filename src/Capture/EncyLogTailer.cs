using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EncyPulse.Core;

namespace EncyPulse.Capture;

/// <summary>Reads new bytes from ENCY's log file on every call and hands them to the parser.</summary>
internal sealed class EncyLogTailer
{
    private readonly EncyLogParser _parser = new();
    private readonly Log _log;
    private string? _path;
    private long _offset;
    private bool _warned;

    public EncyLogTailer(Log log) => _log = log;

    public string? Path => _path;

    /// <summary>Start (or restart) at the end of the given file: only new records count.</summary>
    public void Attach(string path)
    {
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;
        _path = path;
        try { _offset = new FileInfo(path).Length; } catch { _offset = 0; }
        _log.Info($"tailing ENCY log {path} from offset {_offset}");
    }

    public IReadOnlyList<LogSignal> ReadNew()
    {
        if (_path == null) return Array.Empty<LogSignal>();
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _offset) _offset = 0; // rotated or truncated
            if (fs.Length == _offset) return Array.Empty<LogSignal>();
            fs.Seek(_offset, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - _offset];
            var read = fs.Read(buffer, 0, buffer.Length);
            _offset += read;
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            return _parser.Feed(text);
        }
        catch (Exception ex)
        {
            if (!_warned) { _warned = true; _log.Warn($"cannot read ENCY log {_path}: {ex.Message}"); }
            return Array.Empty<LogSignal>();
        }
    }
}
