using System.Diagnostics;
using CAMAPI.Application;
using CAMAPI.DotnetHelper;
using CAMAPI.Extensions;
using CAMAPI.Project;
using CAMAPI.ResultStatus;

namespace EncyExtension;

/// <summary>
/// Example utility: dumps the active project's path and id into a text file and opens it.
/// Replace this with your extension logic.
/// </summary>
public class UtilityExtension : IExtension, IExtensionUtility
{
    /// <inheritdoc />
    public IExtensionInfo? Info { get; set; }

    /// <summary>
    /// Called when the user runs the utility from ENCY.
    /// </summary>
    /// <param name="context">Information about the current ENCY instance.</param>
    /// <param name="resultStatus">Error reporting (exceptions do not cross the host boundary).</param>
    public void Run(IExtensionUtilityContext context, out TResultStatus resultStatus)
    {
        resultStatus = default;
        try
        {
            using var projectCom = new ComWrapper<ICamApiProject>(
                context.CamApplication.GetActiveProject(out resultStatus));
            if (resultStatus.Code == TResultStatusCode.rsError)
                throw new Exception("Error getting project: " + resultStatus.Description);
            if (projectCom.IsNull)
                throw new Exception("No active project");

            // Talk to the COM object through Invoke: it runs the call on the thread the object
            // belongs to. Reading .Instance directly is marked obsolete for exactly that reason.
            string filePath = projectCom.Invoke(p => p.FilePath);
            string id = projectCom.Invoke(p => p.Id);

            var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
            File.WriteAllText(tempFile,
                "Hello from EncyExtension!" + Environment.NewLine +
                "Project file path: " + filePath + Environment.NewLine +
                "Project id: " + id);
            Process.Start("notepad.exe", tempFile);
        }
        catch (Exception e)
        {
            resultStatus.Code = TResultStatusCode.rsError;
            resultStatus.Description = e.Message;
        }
    }
}
