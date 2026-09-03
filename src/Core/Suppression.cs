using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace EncyPulse.Core;

public enum SuppressionAction { Send, Drop, Defer }

public readonly record struct SuppressionDecision(SuppressionAction Action, DateTimeOffset? Until, string Reason)
{
    public static SuppressionDecision Send() => new(SuppressionAction.Send, null, "");
    public static SuppressionDecision Drop(string why) => new(SuppressionAction.Drop, null, why);
    public static SuppressionDecision Defer(DateTimeOffset until, string why) => new(SuppressionAction.Defer, until, why);
}

/// <summary>Decides whether a notification goes out now, later, or not at all.</summary>
public sealed class Suppression
{
    private readonly Func<double> _idleSeconds;
    private readonly Func<Settings> _settings;

    /// <param name="idleSeconds">Seconds since the last keyboard/mouse input; negative when unknown.</param>
    public Suppression(Func<double> idleSeconds, Func<Settings> settings)
    {
        _idleSeconds = idleSeconds;
        _settings = settings;
    }

    public SuppressionDecision Decide(Notification n, DateTimeOffset now)
    {
        if (n.Kind == NotificationKind.Test) return SuppressionDecision.Send();
        var d = _settings().Defaults;

        if (d.OnlyWhenAwayMinutes > 0)
        {
            var idle = _idleSeconds();
            if (idle >= 0 && idle < d.OnlyWhenAwayMinutes * 60)
                return SuppressionDecision.Drop($"user is at the workstation (idle {idle:F0} s)");
        }

        if (n.Channels.Any(ChannelKeys.IsPush) &&
            InQuietHours(d.QuietHoursFrom, d.QuietHoursTo, now.ToLocalTime(), out var until))
            return SuppressionDecision.Defer(until, $"quiet hours until {until.ToLocalTime():HH:mm}");

        return SuppressionDecision.Send();
    }

    /// <summary>True when local time is inside [from, to). Windows crossing midnight are supported.</summary>
    public static bool InQuietHours(string from, string to, DateTimeOffset localNow, out DateTimeOffset until)
    {
        until = default;
        if (!TimeOnly.TryParseExact(from?.Trim() ?? "", "HH:mm", out var f) ||
            !TimeOnly.TryParseExact(to?.Trim() ?? "", "HH:mm", out var t) || f == t)
            return false;

        var nowT = TimeOnly.FromDateTime(localNow.DateTime);
        var today = localNow.Date;
        bool inside;
        DateTime end;
        if (f < t)
        {
            inside = nowT >= f && nowT < t;
            end = today.Add(t.ToTimeSpan());
        }
        else
        {
            inside = nowT >= f || nowT < t;
            end = nowT >= f ? today.AddDays(1).Add(t.ToTimeSpan()) : today.Add(t.ToTimeSpan());
        }
        if (!inside) return false;
        until = new DateTimeOffset(end, localNow.Offset);
        return true;
    }
}

/// <summary>Seconds since the last keyboard or mouse input on this Windows session.</summary>
public static class WinIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static double GetIdleSeconds()
    {
        if (!OperatingSystem.IsWindows()) return -1;
        try
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref li)) return -1;
            var now = unchecked((uint)Environment.TickCount);
            return unchecked(now - li.dwTime) / 1000.0;
        }
        catch { return -1; }
    }
}
