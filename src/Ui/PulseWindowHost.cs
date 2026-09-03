using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using EncyPulse.Capture;

namespace EncyPulse.Ui;

/// <summary>
/// Hosts the ENCY Pulse window on its own STA thread with its own dispatcher. ENCY's thread never
/// waits for it: Show returns at once, a second call brings the open window to the front, and the
/// thread ends by itself when the window closes.
/// </summary>
internal static class PulseWindowHost
{
    private static readonly object Gate = new();
    private static PulseWindow? _window;
    private static Dispatcher? _dispatcher;

    public static bool IsOpen
    {
        get { lock (Gate) return _window != null; }
    }

    /// <summary>Opens the window, or activates it when it is already open. Never blocks the caller.</summary>
    public static void ShowOrActivate(PulseWindowData data)
    {
        lock (Gate)
        {
            if (_window != null && _dispatcher != null)
            {
                var w = _window;
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                        w.Activate();
                        w.Topmost = true; w.Topmost = false; // bring in front of ENCY once
                    }
                    catch (Exception ex) { Runtime.Log.Warn($"activate window: {ex.Message}"); }
                }));
                return;
            }

            var thread = new Thread(() => WindowThread(data))
            {
                Name = "ENCY Pulse window",
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    /// <summary>Closes the window if open (ENCY is shutting down). Does not wait.</summary>
    public static void Close()
    {
        Dispatcher? d;
        PulseWindow? w;
        lock (Gate) { d = _dispatcher; w = _window; }
        if (d == null || w == null) return;
        try { d.BeginInvoke(new Action(() => { try { w.Close(); } catch { } })); } catch { }
    }

    private static void WindowThread(PulseWindowData data)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // An error inside a button handler must not take the window (or its thread) down.
            Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
            {
                Runtime.Log.Error("ENCY Pulse window: unhandled error in a handler", e.Exception);
                try
                {
                    MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\nThe window stays open; details are in the log.",
                        "ENCY Pulse", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
                e.Handled = true;
            };

            var window = new PulseWindow(data);
            lock (Gate)
            {
                _window = window;
                _dispatcher = Dispatcher.CurrentDispatcher;
            }
            window.Closed += (_, _) =>
            {
                lock (Gate) { _window = null; _dispatcher = null; }
                Runtime.Log.Debug("ENCY Pulse window closed");
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            };
            window.Show();
            Runtime.Log.Debug($"ENCY Pulse window shown in {sw.ElapsedMilliseconds} ms");
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            lock (Gate) { _window = null; _dispatcher = null; }
            Runtime.Log.Error("ENCY Pulse window failed", ex);
            try
            {
                MessageBox.Show("The ENCY Pulse window could not be opened:\n\n" + ex.Message +
                                "\n\nDetails were written to the log in " + data.DataDir,
                    "ENCY Pulse", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* nothing else to try */ }
        }
    }
}
