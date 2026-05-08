// -----------------------------------------------------------------------
// <copyright file="IToolApprovalMatcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
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
    /// Returns the exact display patterns shown to the user in the approval
    /// prompt body. For shell these are normalized approval units (verb
    /// chain plus any path-aware first argument); for other tools the tool
    /// name. Reused as the retry-exact key for one-shot approvals.
    /// </summary>
    IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Returns the candidate verb chains evaluated against persisted
    /// <see cref="ApprovalEntry"/> records by the gate. The directory half of
    /// each <c>(verb, directory)</c> pair comes from the candidate's
    /// <see cref="ToolExecutionContext.Cwd"/>, not from extraction. For shell
    /// these are pure verb chains (e.g., <c>git push</c>, <c>grep</c>); for
    /// other tools typically <c>[toolName.Value]</c>.
    /// </summary>
    IReadOnlyList<string> ExtractCandidateVerbs(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Returns true when every candidate verb chain finds a matching
    /// <see cref="ApprovalEntry"/> under the supplied <paramref name="cwd"/>.
    /// A folder-scoped entry matches when its directory contains the cwd and
    /// no symlink segments exist between the two; a global-wildcard entry
    /// (<c>directory: null</c>) matches any cwd.
    /// </summary>
    bool IsApproved(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        IReadOnlyList<ApprovalEntry> approvedEntries,
        string? cwd);

    /// <summary>
    /// Formats the tool call for display in the approval prompt header.
    /// </summary>
    string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments);
}

/// <summary>
/// Shell-specific approval matcher. Verb-chain extraction stops at the first
/// flag, path, or URL token; <c>&amp;&amp;</c> / <c>||</c> / <c>;</c> split
/// approval units while <c>|</c> stays inside one unit; <c>bash -c</c> /
/// <c>sh -c</c> wrappers recurse into the inner command.
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

    public IReadOnlyList<string> ExtractCandidateVerbs(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return [];

        // v2 candidate extraction: verb chains only. The directory half of
        // each (verb, directory) approval pair is the candidate's cwd from
        // ToolExecutionContext, evaluated by the gate. v1's mingling of verb
        // chains, normalized commands, and bare directory roots in this same
        // list was the source of the unreviewable approval store the v2
        // schema set out to fix; we no longer fall back to anything other
        // than the verb chain.
        var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TraverseApprovalUnits(command, unit =>
        {
            var verb = ShellTokenizer.ExtractVerbChain(unit);
            if (!string.IsNullOrEmpty(verb))
                verbs.Add(verb);
        });

        return verbs.ToList();
    }

    public bool IsApproved(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        IReadOnlyList<ApprovalEntry> approvedEntries,
        string? cwd)
    {
        var verbs = ExtractCandidateVerbs(toolName, arguments);
        if (verbs.Count == 0)
            return true; // empty command, nothing to approve

        foreach (var verb in verbs)
        {
            if (!ApprovalPatternMatching.MatchesShellApproval(verb, cwd, approvedEntries))
                return false;
        }

        return true;
    }

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
        => GetCommand(arguments) ?? "(empty command)";

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
/// level — either the tool is approved or it isn't. Directory scoping does
/// not apply.
/// </summary>
public sealed class DefaultApprovalMatcher : IToolApprovalMatcher
{
    public static readonly DefaultApprovalMatcher Instance = new();

    public string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;

    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => false;

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
        => [toolName.Value];

    public IReadOnlyList<string> ExtractCandidateVerbs(ToolName toolName, IDictionary<string, object?>? arguments)
        => [toolName.Value];

    public bool IsApproved(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        IReadOnlyList<ApprovalEntry> approvedEntries,
        string? cwd)
        => ApprovalPatternMatching.MatchesAny(toolName.Value, approvedEntries);

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;
}
