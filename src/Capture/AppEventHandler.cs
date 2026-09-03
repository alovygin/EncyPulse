using System;
using CAMAPI.Application;
using CAMAPI.EventHandler;
using CAMAPI.Project;
using EncyPulse.Core;

namespace EncyPulse.Capture;

/// <summary>
/// Application-level hooks. Every method runs on ENCY's thread and must return quickly.
/// Parameters handed in by ENCY are not stored or wrapped; the runtime re-acquires what it needs.
/// </summary>
internal sealed class AppEventHandler :
    ICamApiEventHandler,
    ICamApiHandlerApplicationAfterLoad,
    ICamApiHandlerApplicationAfterLoadProject,
    ICamApiHandlerApplicationBeforeLoadProject,
    ICamApiHandlerApplicationActiveProjectChanged,
    ICamApiHandlerApplicationNewProject,
    ICamApiHandlerApplicationBeforeClose,
    ICamApiHandlerApplicationUpdateProcessState
{
    public static readonly Type[] HandledInterfaces =
    {
        typeof(ICamApiHandlerApplicationAfterLoad),
        typeof(ICamApiHandlerApplicationAfterLoadProject),
        typeof(ICamApiHandlerApplicationBeforeLoadProject),
        typeof(ICamApiHandlerApplicationActiveProjectChanged),
        typeof(ICamApiHandlerApplicationNewProject),
        typeof(ICamApiHandlerApplicationBeforeClose),
        typeof(ICamApiHandlerApplicationUpdateProcessState),
    };

    /// <summary>Synchronous: the objects ENCY passes are guaranteed alive for the duration of the call.</summary>
    public bool GetAsyncMode(string interfaceUid) => false;

    public void ApplicationAfterLoad(string handlerIdent, ICamApiApplication application) =>
        Safe("AfterLoad", Runtime.OnApplicationLoaded);

    public void ApplicationBeforeLoadProject(string handlerIdent, ICamApiProject project) =>
        Safe("BeforeLoadProject", () =>
        {
            Runtime.NoteProjectLoaded();
            Runtime.UnbindProject();
        });

    /// <summary>The tree may have been rebuilt during the load: hook again from scratch.</summary>
    public void ApplicationAfterLoadProject(string handlerIdent, ICamApiProject project) =>
        Safe("AfterLoadProject", () =>
        {
            Runtime.NoteProjectLoaded();
            Runtime.BindActiveProject(force: true);
        });

    public void ApplicationActiveProjectChanged(string handlerIdent, ICamApiProject newProject) =>
        Safe("ActiveProjectChanged", () =>
        {
            Runtime.NoteProjectLoaded();
            if (newProject == null) Runtime.UnbindProject();
            else Runtime.BindActiveProject();
        });

    public void ApplicationNewProject(string handlerIdent) =>
        Safe("NewProject", Runtime.UnbindProject);

    public void ApplicationBeforeClose(string handlerIdent, ICamApiApplication application) =>
        Safe("BeforeClose", Runtime.UnbindProject);

    public void ApplicationUpdateProcessState(string handlerIdent, string processStageCaption, int processStagePercent)
    {
        try { Runtime.Dispatcher?.Post(new ProgressEvent(processStageCaption ?? "", processStagePercent, DateTimeOffset.UtcNow)); }
        catch { /* never throw into ENCY */ }
    }

    private static void Safe(string what, Action action)
    {
        try
        {
            Runtime.Log.Debug($"application event: {what}");
            action();
        }
        catch (Exception ex)
        {
            Runtime.Log.Error($"application event {what} failed", ex);
        }
    }
}
