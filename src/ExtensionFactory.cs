using EncyPulse;

// The namespace and class name must stay CAMAPI.ExtensionFactory — that is how ENCY finds
// the entry point of the library.
namespace CAMAPI;

using Extensions;
using ResultStatus;

/// <summary>
/// Factory creating the extensions of this library.
/// </summary>
public class ExtensionFactory : IExtensionFactory
{
    /// <inheritdoc />
    public void OnLibraryRegistered(IExtensionFactoryContext context, out TResultStatus ret)
    {
        ret = default;
    }

    /// <inheritdoc />
    public void OnLibraryUnRegistered(IExtensionFactoryContext context, out TResultStatus ret)
    {
        ret = default;
    }

    /// <summary>
    /// Create an extension instance by its identifier. The identifier must match the "id"
    /// declared in EncyPulse.settings.json.
    /// </summary>
    public IExtension? Create(string extensionIdent, out TResultStatus ret)
    {
        try
        {
            ret = default;
            if (extensionIdent == "Extension.Utility.EncyPulse")
                return new UtilityExtension();
            throw new Exception("Unknown extension identifier: " + extensionIdent);
        }
        catch (Exception e)
        {
            ret.Code = TResultStatusCode.rsError;
            ret.Description = e.Message;
        }
        return null;
    }
}
