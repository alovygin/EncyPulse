using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EncyPulse.Capture;

namespace EncyPulse.Ui;

/// <summary>
/// Shows how alerts arrive. Plays welcome.mp4 when one ships next to the extension (or lies in the
/// data folder); otherwise runs a built-in animated illustration of a phone and a watch.
/// </summary>
public partial class DemoPlayer : UserControl
{
    private bool _playing;

    public DemoPlayer()
    {
        InitializeComponent();
        Loaded += (_, _) => TryStartVideo();
        Unloaded += (_, _) => { try { Video.Stop(); } catch { } };
    }

    /// <summary>The recording the extension looks for, in order: data folder, then the extension folder.</summary>
    public static string? FindVideo()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(Runtime.DataDir, "welcome.mp4"),
                Path.Combine(Path.GetDirectoryName(typeof(DemoPlayer).Assembly.Location) ?? "", "welcome.mp4"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
        }
        catch { }
        return null;
    }

    private void TryStartVideo()
    {
        var path = FindVideo();
        if (path == null) return;
        try
        {
            Video.Source = new Uri(path, UriKind.Absolute);
            VideoHost.Visibility = Visibility.Visible;
            Illustration.Visibility = Visibility.Collapsed;
            Video.Play();
            _playing = true;
            PlayPause.Content = "Pause";
        }
        catch (Exception ex)
        {
            Runtime.Log.Warn($"welcome video could not be played: {ex.Message}");
            ShowIllustration();
        }
    }

    private void ShowIllustration()
    {
        VideoHost.Visibility = Visibility.Collapsed;
        Illustration.Visibility = Visibility.Visible;
    }

    private void Video_MediaEnded(object sender, RoutedEventArgs e)
    {
        try { Video.Position = TimeSpan.Zero; Video.Play(); } catch { }
    }

    private void Video_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Runtime.Log.Warn($"welcome video failed: {e.ErrorException.Message}");
        ShowIllustration();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_playing) { Video.Pause(); PlayPause.Content = "Play"; }
            else { Video.Play(); PlayPause.Content = "Pause"; }
            _playing = !_playing;
        }
        catch { }
    }
}
