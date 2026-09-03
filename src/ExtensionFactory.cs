using System;
using EncyPulse.Capture;
using EncyPulse.Ui;

// The namespace and class name must stay CAMAPI.ExtensionFactory: that is how ENCY finds the entry point.
namespace CAMAPI;

using Extensions;
using ResultStatus;

public class ExtensionFactory : IExtensionFactory
{
    public const string GlobalId = "Extension.Global.EncyPulse";
    public const string SettingsId = "Extension.Utility.EncyPulse.Settings";
    public const string PopupId = "Extension.OperationPopup.EncyPulse";

    public void OnLibraryRegistered(IExtensionFactoryContext context, out TResultStatus ret) => ret = default;

    public void OnLibraryUnRegistered(IExtensionFactoryContext context, out TResultStatus ret) => ret = default;

    public IExtension? Create(string extensionIdent, out TResultStatus ret)
    {
        ret = default;
        try
        {
            return extensionIdent switch
            {
                GlobalId => new GlobalExtension(),
                SettingsId => new SettingsUtility(),
                PopupId => new NotifyPopup(),
                _ => throw new Exception("Unknown extension identifier: " + extensionIdent),
            };
        }
        catch (Exception e)
        {
            ret.Code = TResultStatusCode.rsError;
            ret.Description = e.Message;
            return null;
        }
    }
}
