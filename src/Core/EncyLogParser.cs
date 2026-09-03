using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EncyPulse.Core;

public enum LogSignalKind
{
    BatchStarted,        // "Generating toolpath started"
    CalcStarted,         // "Start the operation calculation: <name>"
    CalcCompleted,       // "The calculation of the operation is completed: <name>.\r\nBody toolpath length: ..."
    CalcFailed,          // any other "The calculation of the operation is <status>: <name>"
    ToolpathReset,       // "Reset operation toolpath: <name>"
    SimulationStarted,   // "The Voxel5D simulation of "<name>" started"
    SimulationFinished,  // "The Voxel5D simulation of "<name>" finished"
    NcGenerationStarted, // "TInpCLDShell.BeginGenerateNC"
    NcGenerationFinished // "TInpCLDShell.EndGenerateNC"
}

public sealed record LogSignal(LogSignalKind Kind, string Name, DateTimeOffset At, string Detail);

/// <summary>
/// Parses ENCY's own log (one JSON object per record, "Msg" on a second physical line) into the
/// handful of signals the extension cares about. ENCY does not raise API events for a UI-triggered
/// calculation, but it does log every step; the log is the most reliable source available.
/// Message texts are those of the English UI.
/// </summary>
public sealed class EncyLogParser
{
    private static readonly Regex Record = new(
        "\\{\\s*\"DT\"\\s*:\\s*\"(?<dt>[^\"]*)\".*?\"Msg\"\\s*:\\s*\"(?<msg>(?:[^\"\\\\]|\\\\.)*)\"\\s*\\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CalcStart = new(@"^Start the operation calculation:\s*(?<name>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex CalcDone = new(@"^The calculation of the operation is (?<status>\w+):\s*(?<name>.+?)\.?\s*(?:\r?\n|$)", RegexOptions.Compiled);
    private static readonly Regex Reset = new(@"^Reset operation toolpath:\s*(?<name>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Sim = new(@"^The \w+ simulation of ""(?<name>.+)"" (?<what>started|finished)\s*$", RegexOptions.Compiled);

    private readonly StringBuilder _pending = new();

    /// <summary>Feed newly read text; returns the signals found. Incomplete trailing records are kept for the next call.</summary>
    public IReadOnlyList<LogSignal> Feed(string chunk)
    {
        _pending.Append(chunk);
        var text = _pending.ToString();
        var result = new List<LogSignal>();
        var lastEnd = 0;
        foreach (Match m in Record.Matches(text))
        {
            lastEnd = m.Index + m.Length;
            var at = ParseTime(m.Groups["dt"].Value);
            var msg = Unescape(m.Groups["msg"].Value);
            var s = Classify(msg, at);
            if (s != null) result.Add(s);
        }
        // Keep whatever follows the last complete record; drop pathological growth.
        _pending.Clear();
        if (lastEnd < text.Length)
        {
            var rest = text.Substring(lastEnd);
            if (rest.Length < 256 * 1024) _pending.Append(rest);
        }
        return result;
    }

    public static LogSignal? Classify(string msg, DateTimeOffset at)
    {
        if (msg.Length == 0) return null;
        if (msg.StartsWith("Generating toolpath started", StringComparison.Ordinal))
            return new LogSignal(LogSignalKind.BatchStarted, "", at, msg);
        if (msg.StartsWith("TInpCLDShell.BeginGenerateNC", StringComparison.Ordinal))
            return new LogSignal(LogSignalKind.NcGenerationStarted, "", at, msg);
        if (msg.StartsWith("TInpCLDShell.EndGenerateNC", StringComparison.Ordinal))
            return new LogSignal(LogSignalKind.NcGenerationFinished, "", at, msg);

        var m = CalcStart.Match(msg);
        if (m.Success) return new LogSignal(LogSignalKind.CalcStarted, m.Groups["name"].Value, at, msg);

        m = CalcDone.Match(msg);
        if (m.Success)
        {
            var ok = m.Groups["status"].Value.Equals("completed", StringComparison.OrdinalIgnoreCase);
            var detail = msg.Contains('\n') ? msg[(msg.IndexOf('\n') + 1)..].Trim() : "";
            return new LogSignal(ok ? LogSignalKind.CalcCompleted : LogSignalKind.CalcFailed, m.Groups["name"].Value.TrimEnd('.'), at, detail);
        }

        m = Reset.Match(msg);
        if (m.Success) return new LogSignal(LogSignalKind.ToolpathReset, m.Groups["name"].Value, at, msg);

        m = Sim.Match(msg);
        if (m.Success)
            return new LogSignal(m.Groups["what"].Value == "started" ? LogSignalKind.SimulationStarted : LogSignalKind.SimulationFinished,
                m.Groups["name"].Value, at, msg);

        return null;
    }

    /// <summary>ENCY writes "dd-MM-yyyy HH:mm:ss.fff" in local time.</summary>
    public static DateTimeOffset ParseTime(string dt)
    {
        if (DateTime.TryParseExact(dt.Trim(), "dd-MM-yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return new DateTimeOffset(local);
        return DateTimeOffset.UtcNow;
    }

    /// <summary>Minimal JSON string unescape (the log is ASCII/UTF-8 with standard escapes).</summary>
    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            var n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u' when i + 4 < s.Length && int.TryParse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code); i += 4; break;
                default: sb.Append(n); break; // \" \\ \/
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Groups per-operation simulation log lines into one run: it ends when no line arrived for the quiet
/// window. ENCY runs an automatic stock simulation after a project loads and after a calculation; the
/// caller decides whether a run counts as user-requested.
/// </summary>
public sealed class SimulationSession
{
    private readonly Func<TimeSpan> _quiet;
    private bool _active;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastAt;
    private int _finished;
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public SimulationSession(Func<TimeSpan> quiet) => _quiet = quiet;

    public bool IsActive => _active;
    public DateTimeOffset StartedAt => _startedAt;

    public void OnSignal(LogSignal s)
    {
        if (s.Kind != LogSignalKind.SimulationStarted && s.Kind != LogSignalKind.SimulationFinished) return;
        if (!_active) { _active = true; _startedAt = s.At; _finished = 0; _names.Clear(); }
        _lastAt = s.At;
        if (s.Kind == LogSignalKind.SimulationFinished) { _finished++; _names.Add(s.Name); }
    }

    /// <summary>
    /// Returns the finished run once the quiet window has passed; null otherwise. "Finished" counts
    /// distinct operations: ENCY simulates some operations twice in one run (e.g. after a re-entry),
    /// and the user thinks in operations, not passes.
    /// </summary>
    public (DateTimeOffset StartedAt, TimeSpan Duration, int Finished, IReadOnlyCollection<string> Names)? Tick(DateTimeOffset now)
    {
        if (!_active || now - _lastAt < _quiet()) return null;
        _active = false;
        return (_startedAt, _lastAt - _startedAt, _names.Count, _names.ToList());
    }

    /// <summary>Total "finished" lines in the current or last run, passes included.</summary>
    public int FinishedPasses => _finished;
}
