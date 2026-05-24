// -----------------------------------------------------------------------
// <copyright file="SectionConfigLookup.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;

namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Shared lookup helpers used by <see cref="ISectionEditor"/> implementations
/// to read values out of <see cref="SectionEditorContext.Config"/>. Extracted
/// so each leaf doesn't re-implement the same <c>JsonElement</c> /
/// <c>IDictionary</c> destructuring boilerplate.
/// </summary>
internal static class SectionConfigLookup
{
    /// <summary>True if any enabled flag under <paramref name="section"/> is true.</summary>
    public static bool IsSectionEnabled(SectionEditorContext context, string section)
    {
        if (!context.TryGetValue($"{section}.Enabled", out var v) || v is null)
            return false;
        return v switch
        {
            bool b => b,
            JsonElement je => je.ValueKind == JsonValueKind.True,
            _ => false,
        };
    }

    /// <summary>Whether a section dictionary exists at the top level.</summary>
    public static bool SectionExists(SectionEditorContext context, string section) =>
        context.TryGetValue(section, out var v) && v is not null;

    /// <summary>Resolve a string value at a dotted path, or empty if absent.</summary>
    public static string GetStringOrEmpty(SectionEditorContext context, string dottedPath)
    {
        if (!context.TryGetValue(dottedPath, out var v) || v is null)
            return string.Empty;
        return v switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? string.Empty,
            _ => v.ToString() ?? string.Empty,
        };
    }

    /// <summary>Count the elements of an array at a dotted path. 0 when absent / not an array.</summary>
    public static int CountArray(SectionEditorContext context, string dottedPath)
    {
        if (!context.TryGetValue(dottedPath, out var v) || v is null)
            return 0;
        return v switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.Array => je.GetArrayLength(),
            System.Collections.ICollection col => col.Count,
            _ => 0,
        };
    }
}
