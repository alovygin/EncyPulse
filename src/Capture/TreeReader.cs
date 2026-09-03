using System;
using System.Collections.Generic;
using System.IO;
using CAMAPI.DotnetHelper;
using CAMAPI.Project;
using CAMAPI.TechOperation;
using EncyPulse.Core;

namespace EncyPulse.Capture;

/// <summary>Copies the operation tree into plain data. A few property reads per operation.</summary>
internal static class TreeReader
{
    public static TreeSnapshot Snapshot(ComWrapper<ICamApiProject> projectCom)
    {
        var path = projectCom.FilePath();
        var nodes = new List<OpNode>();
        using var techCom = projectCom.Technologist();
        if (!techCom.IsNull)
        {
            foreach (var opCom in techCom.EnumerateOperations(TCamApiReorderingMode.rmDesigned))
            {
                using (opCom)
                {
                    string? parentId = null;
                    try
                    {
                        using var parentCom = opCom.GetParentOperation(TCamApiReorderingMode.rmDesigned);
                        if (!parentCom.IsNull) parentId = parentCom.Id();
                    }
                    catch { /* root */ }

                    var isGroup = opCom.IsGroup();
                    nodes.Add(new OpNode(
                        Id: opCom.Id(),
                        Name: opCom.Name(),
                        FullName: opCom.FullName(),
                        ParentId: parentId,
                        IsGroup: isGroup,
                        Enabled: SafeBool(() => opCom.Enabled(), true),
                        Calculated: !isGroup && SafeBool(() => opCom.Calculated(), false),
                        HasToolpath: !isGroup && SafeBool(() => opCom.HasToolpath(), false),
                        IsError: SafeBool(() => opCom.IsError(), false),
                        // ICamApiTechOperation.Simulated is the simulator's own flag; IsMachiningResultCalculated is
                        // also set by a plain toolpath calculation and therefore useless for detecting simulations.
                        Simulated: !isGroup && SafeBool(() => opCom.Simulated(), false)));
                }
            }
        }
        return new TreeSnapshot
        {
            ProjectId = projectCom.Id(),
            ProjectName = string.IsNullOrEmpty(path) ? "Untitled project" : Path.GetFileName(path),
            ProjectPath = path,
            Nodes = nodes,
        };
    }

    private static bool SafeBool(Func<bool> f, bool fallback)
    {
        try { return f(); } catch { return fallback; }
    }
}
