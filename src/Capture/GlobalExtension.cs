using System;
using CAMAPI.Extensions;
using CAMAPI.ResultStatus;

namespace EncyPulse.Capture;

/// <summary>Starts the runtime with ENCY and stops it on shutdown. No UI of its own.</summary>
public sealed class GlobalExtension : IExtension, IExtensionGlobal
{
    public IExtensionInfo? Info { get; set; }

    public TResultStatus OnSCInitializing()
    {
        TResultStatus status = default;
        try
        {
            Runtime.Start();
        }
        catch (Exception e)
        {
            try { Runtime.Log.Error("OnSCInitializing", e); } catch { }
            status.Code = TResultStatusCode.rsError;
            status.Description = "ENCY Pulse could not start: " + e.Message;
        }
        return status;
    }

    public TResultStatus OnSCFinalizing()
    {
        TResultStatus status = default;
        try
        {
            Runtime.Stop();
        }
        catch (Exception e)
        {
            status.Code = TResultStatusCode.rsError;
            status.Description = "ENCY Pulse could not stop cleanly: " + e.Message;
        }
        return status;
    }
}
