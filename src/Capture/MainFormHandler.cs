using System;
using CAMAPI.ApplicationMainForm;
using CAMAPI.EventHandler;
using EncyPulse.Core;

namespace EncyPulse.Capture;

/// <summary>
/// The main window's progress indicator (show / progress / hide). A second signal for batch
/// boundaries, independent of the application-level UpdateProcessState hook.
/// </summary>
internal sealed class MainFormHandler : ICamApiEventHandler, ICamApiHandlerProgressIndicator
{
    public const string Ident = "EncyPulse.MainForm";

    public bool GetAsyncMode(string interfaceUid) => false;

    public void ProgressIndicatorEvent(string handlerIdent, TProgressIndicatorEventType eventType)
    {
        try
        {
            if (eventType != TProgressIndicatorEventType.pietProgress)
                Runtime.Log.Debug($"progress indicator: {eventType}");
            var percent = eventType == TProgressIndicatorEventType.pietHide ? 100 : 0;
            Runtime.Dispatcher?.Post(new ProgressEvent($"indicator:{eventType}", percent, DateTimeOffset.UtcNow));
        }
        catch { /* never throw into ENCY */ }
    }
}
