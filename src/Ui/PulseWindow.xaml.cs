using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using EncyPulse.Capture;
using EncyPulse.Core;

namespace EncyPulse.Ui;

/// <summary>The ENCY Pulse window. Runs on its own STA thread; touches no COM objects.</summary>
public partial class PulseWindow : Window
{
    private readonly PulseViewModel _vm;
    private readonly PulseWindowData _data;
    private DispatcherTimer? _testTimer;
    private string? _testId;
    private DateTime _testStarted;

    public PulseWindow(PulseWindowData data)
    {
        _data = data;
        InitializeComponent();
        _effectiveDark = data.DarkTheme;
        ApplyTheme(data.Palette, data.DarkTheme);
        _vm = new PulseViewModel(data);
        DataContext = _vm;

        NtfyTokenBox.Password = _vm.NtfyToken;
        PushoverUserBox.Password = _vm.PushoverUser;
        PushoverTokenBox.Password = _vm.PushoverToken;
        SmtpPasswordBox.Password = _vm.SmtpPassword;
        RelayKeyBox.Password = _vm.RelayKey;

        if (data.OwnerHandle != IntPtr.Zero)
        {
            new WindowInteropHelper(this).Owner = data.OwnerHandle;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // First launch: the Overview with the demo. Afterwards: Delivery until a channel exists, then Notifications.
        if (!data.Settings.Appearance.WelcomeShown)
        {
            NavOverview.IsChecked = true;
            Closed += (_, _) =>
            {
                try { Runtime.Settings.Update(s => s.Appearance.WelcomeShown = true); } catch { }
            };
        }
        else if (!data.Settings.EnabledChannels().Any()) NavDelivery.IsChecked = true;

        // Appearance: "auto" follows ENCY, "light"/"dark" force a built-in palette. Checking the radio applies it.
        _themeReady = true;
        switch ((data.Settings.Appearance.Theme ?? "auto").ToLowerInvariant())
        {
            case "light": ThemeLight.IsChecked = true; break;
            case "dark": ThemeDark.IsChecked = true; break;
            default: ThemeAuto.IsChecked = true; break;
        }
        SourceInitialized += (_, _) => ApplyTitleBarTheme(_effectiveDark);
    }

    private bool _themeReady;
    private bool _effectiveDark;

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_themeReady) return;
        var mode = (sender as RadioButton)?.Tag as string ?? "auto";
        switch (mode)
        {
            case "light": _effectiveDark = false; ApplyTheme(_data.DarkTheme ? null : _data.Palette, false); break;
            case "dark": _effectiveDark = true; ApplyTheme(_data.DarkTheme ? _data.Palette : null, true); break;
            default: _effectiveDark = _data.DarkTheme; ApplyTheme(_data.Palette, _data.DarkTheme); break;
        }
        ApplyTitleBarTheme(_effectiveDark);

        // Remember the choice right away; it is an appearance preference, not part of the rules.
        if (_data.Settings.Appearance.Theme != mode)
        {
            _data.Settings.Appearance.Theme = mode;
            try { Runtime.Settings.Update(s => s.Appearance.Theme = mode); } catch (Exception ex) { Runtime.Log.Warn($"could not save appearance: {ex.Message}"); }
        }
    }

    // ---- theme -----------------------------------------------------------------------------------

    /// <summary>
    /// Builds the brush set from ENCY's live palette (window, panel, text, accent, title, button,
    /// border). Missing entries fall back to hand-tuned colours that match ENCY 3's dark and light looks.
    /// </summary>
    private void ApplyTheme(EncyPalette? p, bool dark)
    {
        Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        var bg = p?.WindowBackground ?? (dark ? C("#1B1E21") : C("#EEF0F2"));
        var surface = p?.PanelBackground ?? (dark ? C("#2A2E32") : C("#FFFFFF"));
        var ink = p?.Text ?? (dark ? C("#E9EBED") : C("#1F2428"));
        var accent = p?.Accent ?? (dark ? C("#22C47E") : C("#12A56A"));
        var line = p?.Border ?? (dark ? C("#41474D") : C("#D3D8DD"));
        var headerBg = p?.TitleBackground ?? (dark ? C("#141618") : C("#FFFFFF"));
        var headerInk = p?.TitleForeground ?? ink;

        // A palette can come back degenerate (e.g. text equal to background); guard the essentials.
        if (Luminance(ink) - Luminance(bg) is > -0.15 and < 0.15) ink = dark ? C("#E9EBED") : C("#1F2428");
        if (Luminance(accent) is < 0.08 or > 0.95) accent = dark ? C("#22C47E") : C("#12A56A");

        // ENCY's "button background" slot is a tinted accent, not a neutral: secondary surfaces are
        // derived from the panel colour instead, so text on them keeps its contrast.
        var button = Blend(ink, surface, 0.07);
        if (Contrast(line, surface) < 1.15) line = Blend(ink, surface, 0.16);

        var ink2 = Blend(ink, bg, 0.62);
        if (Contrast(ink2, button) < 3.5) ink2 = Blend(ink, bg, 0.78);
        var accentSoft = Blend(accent, surface, 0.18);
        var accentInk = Luminance(accent) > 0.45 ? C("#0B1F17") : Colors.White;
        var warn = dark ? C("#E0A24A") : C("#B9781C");
        var ok = dark ? C("#5FC08A") : C("#2E7D4F");
        var danger = dark ? C("#F28B82") : C("#B3261E");

        void B(string key, Color c) => Resources[key] = new SolidColorBrush(c);
        B("BgBrush", bg);
        B("SurfaceBrush", surface);
        B("Surface2Brush", button);
        B("InkBrush", ink);
        B("Ink2Brush", ink2);
        B("LineBrush", line);
        B("AccentBrush", accent);
        B("AccentInkBrush", accentInk);
        B("AccentSoftBrush", accentSoft);
        B("HeaderBgBrush", headerBg);
        B("HeaderInkBrush", Luminance(headerInk) - Luminance(headerBg) is > -0.15 and < 0.15 ? ink : headerInk);
        B("WarnBrush", warn);
        B("WarnSoftBrush", Blend(warn, surface, 0.18));
        B("OkBrush", ok);
        B("DangerBrush", danger);
    }

    private static Color Blend(Color a, Color b, double amountOfA)
    {
        var t = Math.Clamp(amountOfA, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R * t + b.R * (1 - t)),
            (byte)Math.Round(a.G * t + b.G * (1 - t)),
            (byte)Math.Round(a.B * t + b.B * (1 - t)));
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>WCAG-style contrast ratio (1 = identical, 21 = black on white).</summary>
    private static double Contrast(Color a, Color b)
    {
        double Lin(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        double L(Color c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        var l1 = L(a); var l2 = L(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private void GetStarted_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.NtfyEnabled || _vm.PushoverEnabled || _vm.EmailEnabled || _vm.RelayEnabled) NavNotifications.IsChecked = true;
        else NavDelivery.IsChecked = true;
    }

    private void CopyTopic_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_vm.NtfyTopic);
            _vm.TopicHint = "Copied. Paste it into the ntfy app as the topic to subscribe to.";
        }
        catch (Exception ex) { _vm.TopicHint = "Could not copy: " + ex.Message; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Windows 10 20H1+ draws the caption bar dark when asked; matches ENCY's dark frame.</summary>
    private void ApplyTitleBarTheme(bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var value = dark ? 1 : 0;
            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch { /* older Windows: default caption */ }
    }

    // ---- navigation -----------------------------------------------------------------------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageOverview == null || PageNotifications == null || PageDelivery == null || PageHelp == null) return;
        var tag = (sender as RadioButton)?.Tag as string;
        PageOverview.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        PageNotifications.Visibility = tag == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
        PageDelivery.Visibility = tag == "Delivery" ? Visibility.Visible : Visibility.Collapsed;
        PageHelp.Visibility = tag == "Help" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- secrets (PasswordBox cannot bind) --------------------------------------------------------

    private void Secret_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box || _vm == null) return;
        switch (box.Tag as string)
        {
            case "NtfyToken": _vm.NtfyToken = box.Password; break;
            case "PushoverUser": _vm.PushoverUser = box.Password; break;
            case "PushoverToken": _vm.PushoverToken = box.Password; break;
            case "SmtpPassword": _vm.SmtpPassword = box.Password; break;
            case "RelayKey": _vm.RelayKey = box.Password; break;
        }
    }

    private void GenerateTopic_Click(object sender, RoutedEventArgs e)
    {
        _vm.NtfyTopic = TopicCode.New();
        _vm.TopicHint = "New code. Subscribe to it in the ntfy app; the old code stops receiving messages after you save.";
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_data.LogPath)) Process.Start(new ProcessStartInfo(_data.LogPath) { UseShellExecute = true });
            else _vm.TestStatus = "No log yet.";
        }
        catch { }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_data.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_data.DataDir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ---- save / test ----------------------------------------------------------------------------

    private bool SaveAll(bool deliveryOnly)
    {
        var problem = _vm.Validate();
        if (problem != null)
        {
            MessageBox.Show(this, problem, "ENCY Pulse", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        try
        {
            var settings = Runtime.Settings.Update(s => _vm.ApplyTo(s));
            if (!deliveryOnly) Runtime.Rules.Update(r => _vm.ApplyTo(r));
            Runtime.ApplyDiagnostics(settings);
            Runtime.Dispatcher?.RefreshSenders();
            Runtime.Log.Info($"settings saved from ENCY Pulse: channels=[{string.Join(",", settings.EnabledChannels())}], project calc={settings.Defaults.NotifyProjectCompleted}, project sim={settings.Defaults.NotifySimulationCompleted}, selected operations={_vm.SelectedCount}");
            return true;
        }
        catch (Exception ex)
        {
            Runtime.Log.Error("saving from ENCY Pulse failed", ex);
            MessageBox.Show(this, "Could not save the settings: " + ex.Message, "ENCY Pulse", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAll(deliveryOnly: false)) return;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void SendTest_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.NtfyEnabled && !_vm.PushoverEnabled && !_vm.EmailEnabled && !_vm.RelayEnabled)
        {
            _vm.TestStatus = "Switch on a channel first.";
            return;
        }
        if (!SaveAll(deliveryOnly: true)) return;
        if (Runtime.Dispatcher == null)
        {
            _vm.TestStatus = "The background service is not running. Restart ENCY and try again.";
            return;
        }

        _testId = Runtime.Dispatcher.SendTest();
        _testStarted = DateTime.UtcNow;
        _vm.TestBusy = true;
        _vm.TestStatus = "Sending…";
        _testTimer?.Stop();
        _testTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _testTimer.Tick += TestTimer_Tick;
        _testTimer.Start();
    }

    private void TestTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var dir = Path.Combine(_data.DataDir, "outbox");
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, $"*-{_testId}.json") : Array.Empty<string>();
            if (files.Length == 0)
            {
                if ((DateTime.UtcNow - _testStarted).TotalSeconds < 2) return; // not yet written
                Finish("Delivered. Check your phone or inbox.");
                return;
            }
            var json = File.ReadAllText(files[0]);
            var err = System.Text.Json.JsonDocument.Parse(json).RootElement.TryGetProperty("lastError", out var le) && le.ValueKind == System.Text.Json.JsonValueKind.String ? le.GetString() : null;
            if (!string.IsNullOrEmpty(err))
            {
                Finish("Not delivered: " + err + "  It will be retried; fix the settings and test again.");
                return;
            }
            if ((DateTime.UtcNow - _testStarted).TotalSeconds > 25)
                Finish("Still sending… the message is queued and will be retried automatically.");
        }
        catch (Exception ex)
        {
            Finish("Could not check the result: " + ex.Message);
        }
    }

    private void Finish(string status)
    {
        _testTimer?.Stop();
        _vm.TestBusy = false;
        _vm.TestStatus = status;
    }
}
