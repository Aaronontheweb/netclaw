// -----------------------------------------------------------------------
// <copyright file="IToolApprovalMatcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;

namespace Netclaw.Security;

/// <summary>
/// Tool-specific pattern extraction and matching for the approval system.
/// Each tool type can provide its own matcher to define what constitutes
/// an "intent-level" pattern for approval purposes.
/// </summary>
public interface IToolApprovalMatcher
{
    /// <summary>
    /// Returns the key used to look up this invocation's approval mode in
    /// <c>ToolApprovalConfig.ToolOverrides</c>. Most matchers return the tool
    /// name unchanged; argument-aware matchers may return a context-specific
    /// key so different invocations of the same tool (e.g., a write to a
    /// control-plane file vs. a write to a user file) can be gated
    /// independently.
    /// </summary>
    string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Returns true if this invocation must require interactive approval on
    /// the Personal audience when no explicit approval policy is configured.
    /// Encapsulates the fail-closed decision so callers do not have to inspect
    /// tool names or approval-key string formats.
    /// </summary>
    bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Extracts the exact approval patterns shown to the user.
    /// For shell: normalized approval units. For other tools: the tool name.
    /// </summary>
    IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Extracts the reusable approval entries consulted for session and persistent
    /// approval checks.
    /// </summary>
    IReadOnlyList<string> ExtractApprovalEntries(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Checks if the tool call matches any approved pattern.
    /// </summary>
    bool IsApproved(ToolName toolName, IDictionary<string, object?>? arguments, IEnumerable<string> approvedPatterns);

    /// <summary>
    /// Formats the tool call for display in the approval prompt.
    /// </summary>
    string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Extracts reusable directory approval roots for the invocation.
    /// </summary>
    IReadOnlyList<DirectoryApprovalRoot> ExtractDirectoryRoots(ToolName toolName, IDictionary<string, object?>? arguments);
}

/// <summary>
/// Shell-specific approval matcher using approval units bounded by &&, ||, and ;.
/// Pipelines remain inside the same approval unit.
/// </summary>
public sealed class ShellApprovalMatcher : IToolApprovalMatcher
{
    public static readonly ShellApprovalMatcher Instance = new();

    public string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;

    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => true;

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TraverseApprovalUnits(command, unit =>
        {
            var normalized = ShellTokenizer.NormalizeApprovalUnit(unit, GetWorkingDirectory(arguments));
            if (!string.IsNullOrEmpty(normalized))
                patterns.Add(normalized);
        });

        return patterns.ToList();
    }

    public IReadOnlyList<string> ExtractApprovalEntries(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return [];

        // Shell approvals intentionally keep two parallel views of the same
        // invocation:
        //
        // 1. `Patterns` are the exact normalized approval units shown in the
        //    prompt and reused only for approve-once retries.
        // 2. `ApprovalEntries` are the broader entries consulted for session
        //    and persistent approval reuse.
        //
        // The directory-scoping algorithm starts here. We first break the
        // command into approval units: &&, ||, and ; split into separate units,
        // while pipelines joined by | stay together as one piece of work.
        // `bash -c` / `sh -c` wrappers recurse into the inner command and feed
        // those inner units back through the same logic.
        //
        // For each unit we try to derive reusable local directory roots. If we
        // can do that safely, those roots become the approval entries recorded
        // for B/C approvals. If we cannot, we fall back to the exact normalized
        // unit. That keeps approve-once exact while letting broader approvals
        // reuse local directory access without introducing verb allowlists.
        var workingDirectory = GetWorkingDirectory(arguments);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TraverseApprovalUnits(command, unit =>
        {
            var roots = ShellTokenizer.ExtractDirectoryRoots(unit, workingDirectory);
            if (roots.Count > 0)
            {
                foreach (var root in roots)
                    entries.Add(root.ComparisonRoot);
                return;
            }

            var normalized = ShellTokenizer.NormalizeApprovalUnit(unit, workingDirectory);
            if (!string.IsNullOrEmpty(normalized))
                entries.Add(normalized);
        });

        return entries.ToList();
    }

    public bool IsApproved(ToolName toolName, IDictionary<string, object?>? arguments, IEnumerable<string> approvedPatterns)
    {
        var approvalEntries = ExtractApprovalEntries(toolName, arguments);
        if (approvalEntries.Count == 0)
            return true; // Empty command, nothing to approve

        var approvedList = approvedPatterns as IReadOnlyList<string> ?? approvedPatterns.ToList();
        foreach (var entry in approvalEntries)
        {
            if (!ApprovalPatternMatching.MatchesShellApprovalEntry(entry, approvedList))
                return false;
        }

        return true;
    }

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        return GetCommand(arguments) ?? "(empty command)";
    }

    public IReadOnlyList<DirectoryApprovalRoot> ExtractDirectoryRoots(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var roots = new List<DirectoryApprovalRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TraverseApprovalUnits(command, unit =>
        {
            foreach (var root in ShellTokenizer.ExtractDirectoryRoots(unit, GetWorkingDirectory(arguments)))
            {
                if (seen.Add(root.ComparisonRoot))
                    roots.Add(root);
            }
        });

        return roots;
    }

    private static string? GetCommand(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;

        if (arguments.TryGetValue("Command", out var val) || arguments.TryGetValue("command", out val))
            return val?.ToString();

        return null;
    }

    private static string? GetWorkingDirectory(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;

        if (arguments.TryGetValue("WorkingDirectory", out var val) || arguments.TryGetValue("workingDirectory", out val))
            return val?.ToString();

        return null;
    }

    private static void TraverseApprovalUnits(string command, Action<string> visitUnit)
    {
        // Approval units recurse through shell wrappers but keep the outer
        // splitting rules stable, so `bash -c "grep ... | wc -l" && git push`
        // still becomes two independent approval decisions.
        foreach (var segment in ShellTokenizer.SplitCompoundCommand(command))
        {
            var innerCommands = ShellTokenizer.ExtractInnerCommands(segment);
            if (innerCommands.Count > 0)
            {
                foreach (var inner in innerCommands)
                    TraverseApprovalUnits(inner, visitUnit);

                continue;
            }

            visitUnit(segment);
        }
    }
}

/// <summary>
/// Default approval matcher for non-shell tools. Approval is at the tool-name
/// level — either the tool is approved or it isn't.
/// </summary>
public sealed class DefaultApprovalMatcher : IToolApprovalMatcher
{
    public static readonly DefaultApprovalMatcher Instance = new();

    public string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;

    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => false;

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        return [toolName.Value];
    }

    public IReadOnlyList<string> ExtractApprovalEntries(ToolName toolName, IDictionary<string, object?>? arguments)
        => ExtractPatterns(toolName, arguments);

    public bool IsApproved(ToolName toolName, IDictionary<string, object?>? arguments, IEnumerable<string> approvedPatterns)
    {
        foreach (var approved in approvedPatterns)
        {
            if (string.Equals(toolName.Value, approved, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        return toolName.Value;
    }

    public IReadOnlyList<DirectoryApprovalRoot> ExtractDirectoryRoots(ToolName toolName, IDictionary<string, object?>? arguments)
        => [];
}
