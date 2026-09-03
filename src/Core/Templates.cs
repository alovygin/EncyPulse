using System;
using System.Linq;
using System.Collections.Generic;
namespace EncyPulse.Core;

/// <summary>English message texts. Titles are the sentence the user reads on a watch or lock screen.</summary>
public static class Templates
{
    public const string ProductName = "ENCY Pulse";

    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return $"{(int)Math.Round(d.TotalSeconds)} s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} m {d.Seconds:00} s";
        return $"{(int)d.TotalHours} h {d.Minutes:00} m";
    }

    public static string Plural(int n, string singular, string? plural = null) =>
        n == 1 ? $"{n} {singular}" : $"{n} {plural ?? singular + "s"}";

    public static string NamesList(IEnumerable<string> names, int max = 5)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (list.Count == 0) return "";
        if (list.Count <= max) return string.Join(", ", list);
        return string.Join(", ", list.Take(max)) + $" and {list.Count - max} more";
    }

    public static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    // ---- Selected operations ------------------------------------------------------------------

    public static (string Title, string Body) OperationCompleted(OperationCalculatedEvent e, TimeSpan? approxDuration) =>
        ($"Operation '{e.Name}' calculation completed.",
         Join(e.ProjectName, approxDuration.HasValue ? Duration(approxDuration.Value) : null));

    public static (string Title, string Body) OperationFailed(OperationCalculatedEvent e) =>
        ($"Operation '{e.Name}' calculation failed.",
         Join(e.ProjectName, e.Calculated ? "no toolpath was produced" : "the calculation did not complete"));

    public static (string Title, string Body) OperationSimulated(string name, string projectName, TimeSpan runDuration) =>
        ($"Simulation completed for '{name}'.",
         Join(projectName, Duration(runDuration)));

    // ---- Entire project -----------------------------------------------------------------------

    public static (string Title, string Body) ProjectCompleted(string projectName, int opCount, TimeSpan duration, IReadOnlyList<string> failedNames) =>
        ("Project calculation completed.",
         Join(projectName, Plural(opCount, "operation"), Duration(duration),
              failedNames.Count > 0 ? $"{Plural(failedNames.Count, "failed")}: {NamesList(failedNames)}" : null));

    public static (string Title, string Body) ProjectSimulated(string projectName, int opCount, TimeSpan duration, int errorCount) =>
        ("Project simulation completed.",
         Join(projectName, Plural(opCount, "operation"), Duration(duration),
              errorCount > 0 ? $"{Plural(errorCount, "operation")} with errors" : "no errors reported"));

    public static (string Title, string Body) BatchFailed(string projectName, IReadOnlyList<string> failedNames, int total) =>
        ("Calculation stopped with errors.",
         Join(projectName, $"{failedNames.Count} of {Plural(total, "operation")} have no toolpath", NamesList(failedNames)));

    public static (string Title, string Body) Test(string machineName) =>
        ($"{ProductName} is connected.",
         $"Test message from {machineName}. You will be notified here when ENCY finishes.");
}
