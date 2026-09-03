using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using CAMAPI.Application;
using CAMAPI.DotnetHelper;
using CAMAPI.Extensions;
using CAMAPI.ResultStatus;
using EncyPulse.Capture;
using EncyPulse.Core;

namespace EncyPulse.Ui;

/// <summary>
/// Utilities menu button "ENCY Pulse". Reads what the window needs on ENCY's thread (a few
/// milliseconds), hands it to the window host and returns immediately. ENCY stays fully responsive
/// while the window is open; the library stays loaded until it is closed.
/// </summary>
public sealed class SettingsUtility : IExtension, IExtensionUtility, IExtensionLazyUnloadable
{
    public IExtensionInfo? Info { get; set; }

    /// <summary>False while the ENCY Pulse window is open, so ENCY does not unload the DLL underneath it.</summary>
    public bool CanUnload
    {
        get => !PulseWindowHost.IsOpen;
        set { /* managed by the window host */ }
    }

    public void Run(IExtensionUtilityContext context, out TResultStatus resultStatus)
    {
        resultStatus = default;
        var sw = Stopwatch.StartNew();
        try
        {
            Runtime.Start(); // no-op when the Global extension already did it

            // Already open: bring it to the front and return.
            if (PulseWindowHost.IsOpen)
            {
                PulseWindowHost.ShowOrActivate(new PulseWindowData());
                return;
            }

            var settings = Clone(Runtime.Settings.Current);
            var rules = Clone(Runtime.Rules.Current);

            string projectId = "", projectName = "", projectPath = "";
            IReadOnlyList<OpNode> nodes = Array.Empty<OpNode>();
            var dark = false;
            EncyPalette? palette = null;
            var owner = IntPtr.Zero;

            using var appCom = ComWrapper.Create(context.CamApplication);
            if (!appCom.IsNull)
            {
                try
                {
                    using var projectCom = appCom.GetActiveProject();
                    if (!projectCom.IsNull)
                    {
                        var snap = TreeReader.Snapshot(projectCom);
                        projectId = snap.ProjectId;
                        projectName = snap.ProjectName;
                        projectPath = snap.ProjectPath;
                        nodes = snap.Nodes;
                    }
                }
                catch (Exception ex) { Runtime.Log.Warn($"could not read the project tree for the window: {ex.Message}"); }

                try
                {
                    using var themeCom = appCom.Theme();
                    if (themeCom != null && !themeCom.IsNull)
                    {
                        dark = themeCom.IsDark();
                        palette = new EncyPalette
                        {
                            WindowBackground = EncyPalette.FromDelphi(themeCom.GetColor(TCamApiColorKind.ckColorWindowBackground)),
                            PanelBackground = EncyPalette.FromDelphi(themeCom.GetColor(TCamApiColorKind.ckColorPanelBackground)),
                            Text = EncyPalette.FromDelphi(themeCom.GetColor(TCamApiColorKind.ckColorText)),
                            Accent = EncyPalette.FromDelphi(themeCom.GetColor(TCamApiColorKind.ckColorAccent)),
                            TitleBackground = EncyPalette.FromDelphi(themeCom.GetColor(TCamApiColorKind.ckColorTitleBackground)),
                            TitleForeground = EncyPalette.FromDelphi(themeCom.GetColor(TCamApiColorKind.ckColorTitleForeground)),
                            ButtonBackground = EncyPalette.FromDelphi(themeCom.GetColor(TCamApiColorKind.ckColorBtnBackground)),
                            Border = EncyPalette.FromDelphi(themeCom.GetColor(TCamApiColorKind.ckColorBorder)),
                        };
                        Runtime.Log.Debug($"ENCY theme '{themeCom.Name()}' dark={dark}: bg={palette.WindowBackground} panel={palette.PanelBackground} text={palette.Text} accent={palette.Accent} title={palette.TitleBackground}/{palette.TitleForeground} btn={palette.ButtonBackground} border={palette.Border}");
                    }
                }
                catch (Exception ex) { Runtime.Log.Debug($"theme palette unavailable: {ex.Message}"); }

                try
                {
                    using var mainFormCom = appCom.MainForm();
                    if (!mainFormCom.IsNull) owner = (IntPtr)mainFormCom.MainWindowHandle();
                }
                catch { /* centre on screen */ }
            }

            var data = new PulseWindowData
            {
                Settings = settings,
                Rules = rules,
                ProjectId = projectId,
                ProjectName = projectName,
                ProjectPath = projectPath,
                Nodes = nodes,
                DarkTheme = dark,
                Palette = palette,
                OwnerHandle = owner,
                Version = Runtime.Version,
                DataDir = Runtime.DataDir,
                LogPath = Runtime.Log.Path,
            };

            PulseWindowHost.ShowOrActivate(data);
            Runtime.Log.Info($"ENCY Pulse window requested ({nodes.Count} tree nodes read in {sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception e)
        {
            try { Runtime.Log.Error("ENCY Pulse utility", e); } catch { }
            resultStatus.Code = TResultStatusCode.rsError;
            resultStatus.Description = e.Message;
        }
    }

    private static T Clone<T>(T value) where T : class, new() =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonFileStore<Settings>.JsonOptions), JsonFileStore<Settings>.JsonOptions) ?? new T();
}
