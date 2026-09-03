using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using EncyPulse.Core;

namespace EncyPulse.Ui;

/// <summary>Everything the window needs, read on ENCY's thread before the window opens. No COM inside.</summary>
public sealed class PulseWindowData
{
    public Settings Settings { get; init; } = new();
    public NotifyRules Rules { get; init; } = new();
    public string ProjectId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public IReadOnlyList<OpNode> Nodes { get; init; } = Array.Empty<OpNode>();
    public bool DarkTheme { get; init; }
    /// <summary>ENCY's live palette (ICamApiTheme); null when unavailable, the window then uses built-in colours.</summary>
    public EncyPalette? Palette { get; init; }
    public IntPtr OwnerHandle { get; init; }
    public string Version { get; init; } = "";
    public string DataDir { get; init; } = "";
    public string LogPath { get; init; } = "";
}

/// <summary>The eight palette slots ENCY exposes through ICamApiTheme, already converted to WPF colours.</summary>
public sealed class EncyPalette
{
    public System.Windows.Media.Color? WindowBackground { get; init; }
    public System.Windows.Media.Color? PanelBackground { get; init; }
    public System.Windows.Media.Color? Text { get; init; }
    public System.Windows.Media.Color? Accent { get; init; }
    public System.Windows.Media.Color? TitleBackground { get; init; }
    public System.Windows.Media.Color? TitleForeground { get; init; }
    public System.Windows.Media.Color? ButtonBackground { get; init; }
    public System.Windows.Media.Color? Border { get; init; }

    /// <summary>Delphi TColor is 0x00BBGGRR; negative values are system colour indexes and are ignored.</summary>
    public static System.Windows.Media.Color? FromDelphi(int color)
    {
        if (color < 0) return null;
        return System.Windows.Media.Color.FromRgb((byte)(color & 0xFF), (byte)((color >> 8) & 0xFF), (byte)((color >> 16) & 0xFF));
    }
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class OperationRow : Observable
{
    private readonly Action _changed;
    private bool _calc, _sim;

    public OperationRow(OpNode node, int depth, Action changed)
    {
        Id = node.Id;
        Name = node.Name;
        FullName = node.FullName;
        IsGroup = node.IsGroup;
        Indent = new Thickness(Math.Max(0, depth - 1) * 18, 0, 0, 0);
        _changed = changed;
    }

    public string Id { get; }
    public string Name { get; }
    public string FullName { get; }
    public bool IsGroup { get; }
    public Thickness Indent { get; }

    public bool Calc { get => _calc; set { if (Set(ref _calc, value)) _changed(); } }
    public bool Sim { get => _sim; set { if (Set(ref _sim, value)) _changed(); } }

    /// <summary>Set without triggering the parent's recomputation (used by select-all).</summary>
    public void SetQuiet(bool? calc, bool? sim)
    {
        if (calc.HasValue && _calc != calc.Value) { _calc = calc.Value; Raise(nameof(Calc)); }
        if (sim.HasValue && _sim != sim.Value) { _sim = sim.Value; Raise(nameof(Sim)); }
    }
}

public sealed class PulseViewModel : Observable
{
    private readonly PulseWindowData _data;
    private bool _projectCalc, _projectSim, _notifyFailures, _onlyWhenAway, _quietEnabled, _ignoreShort, _debugLog;
    private string _awayMinutes = "2", _quietFrom = "22:00", _quietTo = "07:00", _ignoreShortSec = "60";
    private bool _ntfyEnabled, _pushoverEnabled, _emailEnabled, _relayEnabled, _smtpSsl = true, _testBusy;
    private string _ntfyServer = "https://ntfy.sh", _ntfyTopic = "", _ntfyToken = "", _pushoverUser = "", _pushoverToken = "";
    private string _emailTo = "", _smtpHost = "", _smtpPort = "587", _smtpUser = "", _smtpPassword = "", _emailFrom = "", _relayUrl = "", _relayKey = "";
    private string _testStatus = "";
    private string _topicHint = "";
    private bool _suspendRecompute;

    public PulseViewModel(PulseWindowData data)
    {
        _data = data;
        var s = data.Settings;
        _projectCalc = s.Defaults.NotifyProjectCompleted;
        _projectSim = s.Defaults.NotifySimulationCompleted;
        _notifyFailures = s.Defaults.NotifyFailures;
        _onlyWhenAway = s.Defaults.OnlyWhenAwayMinutes > 0;
        _awayMinutes = (s.Defaults.OnlyWhenAwayMinutes > 0 ? s.Defaults.OnlyWhenAwayMinutes : 2).ToString(CultureInfo.InvariantCulture);
        _quietEnabled = !string.IsNullOrWhiteSpace(s.Defaults.QuietHoursFrom) && !string.IsNullOrWhiteSpace(s.Defaults.QuietHoursTo);
        if (_quietEnabled) { _quietFrom = s.Defaults.QuietHoursFrom; _quietTo = s.Defaults.QuietHoursTo; }
        _ignoreShort = s.Defaults.IgnoreShorterThanSec > 0;
        if (_ignoreShort) _ignoreShortSec = s.Defaults.IgnoreShorterThanSec.ToString(CultureInfo.InvariantCulture);
        _debugLog = s.Diagnostics.DebugLog;

        _ntfyEnabled = s.Channels.Ntfy.Enabled;
        _ntfyServer = string.IsNullOrWhiteSpace(s.Channels.Ntfy.Server) ? "https://ntfy.sh" : s.Channels.Ntfy.Server;
        _ntfyTopic = s.Channels.Ntfy.Topic;
        if (string.IsNullOrWhiteSpace(_ntfyTopic))
        {
            _ntfyTopic = TopicCode.New();
            _topicHint = "This code was generated for you. Enter it in the ntfy app, then press Save.";
        }
        else
        {
            _topicHint = "Your personal code. Anyone who knows it can read your messages, so keep it to yourself.";
        }
        _ntfyToken = Secrets.Reveal(s.Channels.Ntfy.AccessToken);
        _pushoverEnabled = s.Channels.Pushover.Enabled;
        _pushoverUser = Secrets.Reveal(s.Channels.Pushover.UserKey);
        _pushoverToken = Secrets.Reveal(s.Channels.Pushover.AppToken);
        _emailEnabled = s.Channels.Email.Enabled;
        _emailTo = s.Channels.Email.Address;
        _smtpHost = s.Channels.Email.SmtpHost;
        _smtpPort = s.Channels.Email.SmtpPort.ToString(CultureInfo.InvariantCulture);
        _smtpSsl = s.Channels.Email.UseSsl;
        _smtpUser = s.Channels.Email.SmtpUser;
        _smtpPassword = Secrets.Reveal(s.Channels.Email.SmtpPassword);
        _emailFrom = s.Channels.Email.From;
        _relayEnabled = s.Channels.Relay.Enabled;
        _relayUrl = s.Channels.Relay.Url;
        _relayKey = Secrets.Reveal(s.Channels.Relay.ApiKey);

        Operations = BuildRows(data);
        Recompute();
    }

    // ---- header / footer ------------------------------------------------------------------------

    public bool HasProject => !string.IsNullOrEmpty(_data.ProjectId) && Operations.Any(o => !o.IsGroup);
    public string ProjectName => HasProject ? _data.ProjectName : "no project open";
    public string VersionText => $"ENCY Pulse {_data.Version}";
    public string DataDir => _data.DataDir;
    public string LogPath => _data.LogPath;
    public string FooterText => "Changes apply when you press Save.";

    public string StatusText
    {
        get
        {
            var on = EnabledChannelNames();
            return on.Count == 0 ? "No delivery channel yet — see Delivery" : "Delivering to " + string.Join(", ", on);
        }
    }

    public bool StatusIsWarning => EnabledChannelNames().Count == 0;

    private List<string> EnabledChannelNames()
    {
        var list = new List<string>();
        if (NtfyEnabled) list.Add("phone (ntfy)");
        if (PushoverEnabled) list.Add("Pushover");
        if (EmailEnabled) list.Add("email");
        if (RelayEnabled) list.Add("relay");
        return list;
    }

    // ---- notifications ------------------------------------------------------------------------

    public bool ProjectCalc { get => _projectCalc; set => Set(ref _projectCalc, value); }
    public bool ProjectSim { get => _projectSim; set => Set(ref _projectSim, value); }

    public ObservableCollection<OperationRow> Operations { get; }

    public bool AllCalc
    {
        get => Operations.Where(o => !o.IsGroup).All(o => o.Calc) && Operations.Any(o => !o.IsGroup);
        set { SetAll(calc: value); }
    }

    public bool AllSim
    {
        get => Operations.Where(o => !o.IsGroup).All(o => o.Sim) && Operations.Any(o => !o.IsGroup);
        set { SetAll(sim: value); }
    }

    private void SetAll(bool? calc = null, bool? sim = null)
    {
        _suspendRecompute = true;
        foreach (var o in Operations.Where(o => !o.IsGroup)) o.SetQuiet(calc, sim);
        _suspendRecompute = false;
        Recompute();
    }

    public string PreviewCalc { get; private set; } = "";
    public string PreviewSim { get; private set; } = "";
    public int SelectedCount { get; private set; }

    private void Recompute()
    {
        if (_suspendRecompute) return;
        var leaves = Operations.Where(o => !o.IsGroup).ToList();
        var firstCalc = leaves.FirstOrDefault(o => o.Calc)?.Name ?? "Roughing 01";
        var firstSim = leaves.FirstOrDefault(o => o.Sim)?.Name ?? "Finishing 02";
        var calcCount = leaves.Count(o => o.Calc);
        var simCount = leaves.Count(o => o.Sim);
        PreviewCalc = calcCount == 0 ? "e.g. “Operation 'Roughing 01' calculation completed.”"
                                     : $"“Operation '{firstCalc}' calculation completed.”" + (calcCount > 1 ? $"  (+{calcCount - 1} more)" : "");
        PreviewSim = simCount == 0 ? "e.g. “Simulation completed for 'Finishing 02'.”"
                                   : $"“Simulation completed for '{firstSim}'.”" + (simCount > 1 ? $"  (+{simCount - 1} more)" : "");
        SelectedCount = leaves.Count(o => o.Calc || o.Sim);
        Raise(nameof(PreviewCalc)); Raise(nameof(PreviewSim)); Raise(nameof(SelectedCount));
        Raise(nameof(AllCalc)); Raise(nameof(AllSim));
    }

    private ObservableCollection<OperationRow> BuildRows(PulseWindowData data)
    {
        var rows = new ObservableCollection<OperationRow>();
        var byId = data.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var children = data.Nodes.Where(n => n.ParentId != null)
                                 .GroupBy(n => n.ParentId!, StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var roots = data.Nodes.Where(n => n.ParentId == null || !byId.ContainsKey(n.ParentId)).ToList();
        var project = data.Rules.For(data.ProjectId);

        void Add(OpNode n, int depth)
        {
            if (!n.IsGroup && !n.Enabled) return;
            var hasLeafBelow = n.IsGroup && HasLeaf(n.Id);
            if (n.IsGroup && !hasLeafBelow) return;
            if (depth > 0 || !n.IsGroup) // the root (machine) node itself is not listed, its children are
            {
                var row = new OperationRow(n, depth, Recompute);
                if (!n.IsGroup && project != null)
                {
                    var rule = NotifyRules.Find(project.Operations, n.Id);
                    if (rule != null) row.SetQuiet(rule.Calculation, rule.Simulation);
                }
                rows.Add(row);
            }
            if (children.TryGetValue(n.Id, out var kids))
                foreach (var k in kids) Add(k, depth + 1);
        }

        bool HasLeaf(string id)
        {
            if (!children.TryGetValue(id, out var kids)) return false;
            return kids.Any(k => (!k.IsGroup && k.Enabled) || (k.IsGroup && HasLeaf(k.Id)));
        }

        foreach (var r in roots) Add(r, 0);
        return rows;
    }

    // ---- options -------------------------------------------------------------------------------

    public bool NotifyFailures { get => _notifyFailures; set => Set(ref _notifyFailures, value); }
    public bool OnlyWhenAway { get => _onlyWhenAway; set => Set(ref _onlyWhenAway, value); }
    public string AwayMinutes { get => _awayMinutes; set => Set(ref _awayMinutes, value); }
    public bool QuietEnabled { get => _quietEnabled; set => Set(ref _quietEnabled, value); }
    public string QuietFrom { get => _quietFrom; set => Set(ref _quietFrom, value); }
    public string QuietTo { get => _quietTo; set => Set(ref _quietTo, value); }
    public bool IgnoreShort { get => _ignoreShort; set => Set(ref _ignoreShort, value); }
    public string IgnoreShortSec { get => _ignoreShortSec; set => Set(ref _ignoreShortSec, value); }
    public bool DebugLog { get => _debugLog; set => Set(ref _debugLog, value); }

    // ---- delivery -------------------------------------------------------------------------------

    public bool NtfyEnabled { get => _ntfyEnabled; set { if (Set(ref _ntfyEnabled, value)) StatusChanged(); } }
    public string NtfyServer { get => _ntfyServer; set => Set(ref _ntfyServer, value); }
    public string NtfyTopic { get => _ntfyTopic; set => Set(ref _ntfyTopic, value); }
    public string TopicHint { get => _topicHint; set => Set(ref _topicHint, value); }
    public string NtfyToken { get => _ntfyToken; set => Set(ref _ntfyToken, value); }
    public bool PushoverEnabled { get => _pushoverEnabled; set { if (Set(ref _pushoverEnabled, value)) StatusChanged(); } }
    public string PushoverUser { get => _pushoverUser; set => Set(ref _pushoverUser, value); }
    public string PushoverToken { get => _pushoverToken; set => Set(ref _pushoverToken, value); }
    public bool EmailEnabled { get => _emailEnabled; set { if (Set(ref _emailEnabled, value)) StatusChanged(); } }
    public string EmailTo { get => _emailTo; set => Set(ref _emailTo, value); }
    public string SmtpHost { get => _smtpHost; set => Set(ref _smtpHost, value); }
    public string SmtpPort { get => _smtpPort; set => Set(ref _smtpPort, value); }
    public bool SmtpSsl { get => _smtpSsl; set => Set(ref _smtpSsl, value); }
    public string SmtpUser { get => _smtpUser; set => Set(ref _smtpUser, value); }
    public string SmtpPassword { get => _smtpPassword; set => Set(ref _smtpPassword, value); }
    public string EmailFrom { get => _emailFrom; set => Set(ref _emailFrom, value); }
    public bool RelayEnabled { get => _relayEnabled; set { if (Set(ref _relayEnabled, value)) StatusChanged(); } }
    public string RelayUrl { get => _relayUrl; set => Set(ref _relayUrl, value); }
    public string RelayKey { get => _relayKey; set => Set(ref _relayKey, value); }
    public string TestStatus { get => _testStatus; set => Set(ref _testStatus, value); }
    public bool TestBusy { get => _testBusy; set => Set(ref _testBusy, value); }

    private void StatusChanged() { Raise(nameof(StatusText)); Raise(nameof(StatusIsWarning)); }

    // ---- apply ----------------------------------------------------------------------------------

    /// <summary>Validation message, or null when everything can be saved.</summary>
    public string? Validate()
    {
        if (NtfyEnabled && string.IsNullOrWhiteSpace(NtfyTopic)) return "Phone push (ntfy) needs a topic. Press Generate to create one.";
        if (NtfyEnabled && !Uri.TryCreate(NtfyServer.Trim(), UriKind.Absolute, out _)) return "The ntfy server must be a full address, e.g. https://ntfy.sh.";
        if (PushoverEnabled && (string.IsNullOrWhiteSpace(PushoverUser) || string.IsNullOrWhiteSpace(PushoverToken))) return "Pushover needs both the user key and the application token.";
        if (EmailEnabled && (string.IsNullOrWhiteSpace(EmailTo) || string.IsNullOrWhiteSpace(SmtpHost))) return "Email needs a recipient address and an SMTP server.";
        if (EmailEnabled && !int.TryParse(SmtpPort.Trim(), out var port) || (EmailEnabled && (SmtpPort.Trim() == "" || int.Parse(SmtpPort.Trim()) <= 0))) return "The SMTP port must be a number, usually 587.";
        if (RelayEnabled && !Uri.TryCreate(RelayUrl.Trim(), UriKind.Absolute, out _)) return "The relay address must be a full https:// address.";
        if (OnlyWhenAway && !int.TryParse(AwayMinutes.Trim(), out var m) || (OnlyWhenAway && int.Parse(AwayMinutes.Trim()) <= 0)) return "“Only when I'm away” needs a number of minutes, e.g. 2.";
        if (QuietEnabled && !(TimeOnly.TryParseExact(QuietFrom.Trim(), "HH:mm", out _) && TimeOnly.TryParseExact(QuietTo.Trim(), "HH:mm", out _))) return "Quiet hours need two times in 24-hour format, e.g. 22:00 and 07:00.";
        if (IgnoreShort && !int.TryParse(IgnoreShortSec.Trim(), out var sec) || (IgnoreShort && int.Parse(IgnoreShortSec.Trim()) <= 0)) return "“Ignore short runs” needs a number of seconds, e.g. 60.";
        return null;
    }

    /// <summary>Write the delivery and option values into a settings object.</summary>
    public void ApplyTo(Settings s)
    {
        s.Defaults.NotifyProjectCompleted = ProjectCalc;
        s.Defaults.NotifySimulationCompleted = ProjectSim;
        s.Defaults.NotifyFailures = NotifyFailures;
        s.Defaults.OnlyWhenAwayMinutes = OnlyWhenAway && int.TryParse(AwayMinutes.Trim(), out var m) ? Math.Max(1, m) : 0;
        s.Defaults.QuietHoursFrom = QuietEnabled ? QuietFrom.Trim() : "";
        s.Defaults.QuietHoursTo = QuietEnabled ? QuietTo.Trim() : "";
        s.Defaults.IgnoreShorterThanSec = IgnoreShort && int.TryParse(IgnoreShortSec.Trim(), out var sec) ? Math.Max(1, sec) : 0;
        s.Diagnostics.DebugLog = DebugLog;

        s.Channels.Ntfy.Enabled = NtfyEnabled;
        s.Channels.Ntfy.Server = NtfyServer.Trim();
        s.Channels.Ntfy.Topic = NtfyTopic.Trim();
        s.Channels.Ntfy.AccessToken = Secrets.Protect(NtfyToken.Trim());
        s.Channels.Pushover.Enabled = PushoverEnabled;
        s.Channels.Pushover.UserKey = Secrets.Protect(PushoverUser.Trim());
        s.Channels.Pushover.AppToken = Secrets.Protect(PushoverToken.Trim());
        s.Channels.Email.Enabled = EmailEnabled;
        s.Channels.Email.Address = EmailTo.Trim();
        s.Channels.Email.SmtpHost = SmtpHost.Trim();
        s.Channels.Email.SmtpPort = int.TryParse(SmtpPort.Trim(), out var port) && port > 0 ? port : 587;
        s.Channels.Email.UseSsl = SmtpSsl;
        s.Channels.Email.SmtpUser = SmtpUser.Trim();
        s.Channels.Email.SmtpPassword = Secrets.Protect(SmtpPassword);
        s.Channels.Email.From = EmailFrom.Trim();
        s.Channels.Relay.Enabled = RelayEnabled;
        s.Channels.Relay.Url = RelayUrl.Trim();
        s.Channels.Relay.ApiKey = Secrets.Protect(RelayKey.Trim());
    }

    /// <summary>Write the per-operation ticks of the current project into the rules.</summary>
    public void ApplyTo(NotifyRules rules)
    {
        if (!HasProject) return;
        foreach (var row in Operations.Where(o => !o.IsGroup))
            rules.SetOperation(_data.ProjectId, _data.ProjectPath, row.Id, row.FullName, row.Calc, row.Sim);
    }
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
