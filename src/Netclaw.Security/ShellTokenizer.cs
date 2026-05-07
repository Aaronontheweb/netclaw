// -----------------------------------------------------------------------
// <copyright file="ShellTokenizer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Security;

/// <summary>
/// Shared tokenizer for shell command strings. Extracts tokens from commands,
/// splits compound commands on operators, and recursively extracts inner
/// commands from bash -c / sh -c wrappers.
/// </summary>
public static class ShellTokenizer
{
    private static readonly string[] CompoundOperators = ["&&", "||"];

    /// <summary>
    /// Verbs that can exfiltrate or mutate file contents when targeting protected paths.
    /// Used by <see cref="ToolPathPolicy"/> for the protected-path heuristic.
    /// </summary>
    internal static readonly HashSet<string> HighRiskVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "cat", "less", "more", "head", "tail", "grep", "rg", "find", "jq", "awk", "sed", "strings", "xxd", "hexdump",
        "cp", "mv", "tar", "zip", "unzip", "scp", "rsync", "curl", "wget", "nc", "ncat",
        "type", "findstr", "copy", "move", "xcopy", "robocopy", "del", "erase", "ren", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "python", "python3", "node", "ruby", "perl", "php",
        "bash", "sh", "zsh"
    };

    /// <summary>
    /// Verbs whose first positional argument is security-relevant for approval
    /// pattern extraction. Superset of <see cref="HighRiskVerbs"/> — includes
    /// benign-but-path-consuming verbs like <c>ls</c>.
    /// </summary>
    internal static readonly HashSet<string> PathAwareVerbs = new(HighRiskVerbs, StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir"
    };

    /// <summary>
    /// Tokenizes a shell command string, respecting single and double quotes.
    /// Strips quote delimiters from tokens.
    /// </summary>
    public static IEnumerable<string> Tokenize(string command)
    {
        var current = new StringBuilder();
        char? quote = null;

        foreach (var ch in command)
        {
            if (quote is null && (ch == '\'' || ch == '"'))
            {
                quote = ch;
                continue;
            }

            if (quote is not null && ch == quote)
            {
                quote = null;
                continue;
            }

            if (quote is null && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    /// <summary>
    /// Splits a compound command on <c>&&</c>, <c>||</c>, and <c>;</c>
    /// operators, returning each approval unit trimmed. Pipes remain inside the
    /// same unit so shell pipelines can be approved as one piece of directory
    /// work.
    /// </summary>
    public static IReadOnlyList<string> SplitCompoundCommand(string command)
        => ShellApprovalSemantics.ForCommand(command).SplitCompoundCommand(command);

    /// <summary>
    /// Extracts the verb chain (command name + subcommands) from a tokenized
    /// command. Stops at the first token that looks like a flag (starts with -)
    /// or an argument (path, URL, etc.), and caps at <paramref name="maxDepth"/>
    /// tokens (default: 2) to avoid capturing positional arguments as subcommands.
    /// For path-aware verbs (cat, grep, bash, etc.), appends the first non-flag
    /// argument so the approval pattern captures what the command operates on.
    /// </summary>
    public static string ExtractVerbChain(string command, int maxDepth = 2)
        => ShellApprovalSemantics.ForCommand(command).ExtractVerbChain(command, maxDepth);

    /// <summary>
    /// Produces an exact shell approval unit string with recognizable local paths
    /// normalized against the working directory. Non-path tokens remain in order.
    /// </summary>
    public static string NormalizeApprovalUnit(string command, string? workingDirectory = null)
        => ShellApprovalSemantics.ForCommand(command).NormalizeApprovalUnit(command, workingDirectory);

    /// <summary>
    /// Extracts reusable directory approval roots from a shell approval unit.
    /// Returns an empty list when no reusable roots can be extracted.
    /// </summary>
    public static IReadOnlyList<DirectoryApprovalRoot> ExtractDirectoryRoots(string command, string? workingDirectory = null)
        => ShellApprovalSemantics.ForCommand(command).ExtractDirectoryRoots(command, workingDirectory);

    /// <summary>
    /// Normalizes a path token using the active shell family's path semantics.
    /// Returns null when the token cannot be normalized as a local path.
    /// </summary>
    public static string? NormalizePathToken(string path, string? workingDirectory = null)
        => ShellApprovalSemantics.ForCommand(path).NormalizePathToken(path, workingDirectory);

    /// <summary>
    /// Extracts inner commands from bash -c / sh -c wrappers. Returns the
    /// inner command strings for recursive scanning. Returns an empty list
    /// if the command does not use a shell wrapper.
    /// </summary>
    public static IReadOnlyList<string> ExtractInnerCommands(string command)
        => ShellApprovalSemantics.ForCommand(command).ExtractInnerCommands(command);

    /// <summary>
    /// Returns all command strings that should be evaluated, including the
    /// top-level compound segments and any recursively extracted inner commands
    /// from bash -c / sh -c wrappers.
    /// </summary>
    public static IReadOnlyList<string> GetAllCommandSegments(string command)
    {
        var allSegments = new List<string>();
        var topLevel = SplitCompoundCommand(command);

        foreach (var segment in topLevel)
        {
            allSegments.Add(segment);

            var innerCommands = ExtractInnerCommands(segment);
            foreach (var inner in innerCommands)
            {
                // Recursively get segments from inner commands
                allSegments.AddRange(GetAllCommandSegments(inner));
            }
        }

        return allSegments;
    }

    /// <summary>
    /// Returns true if a token is identifiable as a local filesystem path.
    /// Uses positive identification (anchored prefixes + extension heuristic)
    /// rather than broad "contains a slash" matching to avoid false positives
    /// on URIs, git refs, docker images, sed expressions, and MIME types.
    /// </summary>
    public static bool LooksLikePath(string token)
        => ShellApprovalSemantics.ForCommand(token).LooksLikePath(token);

    internal const int MinDirectoryScopeDepth = 2;

    /// <summary>
    /// Returns true when the pattern is a single-token shell approval for a
    /// path-aware verb such as <c>cat</c> or <c>bash</c>.
    /// </summary>
    public static bool IsSingleTokenPathAwarePattern(string pattern)
    {
        var trimmed = TrimShellPunctuation(pattern).Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.IndexOfAny([' ', '\t', '\n', '\r']) >= 0)
        {
            return false;
        }

        return PathAwareVerbs.Contains(trimmed);
    }

    internal static string TrimShellPunctuation(string token)
    {
        return token.Trim().TrimStart(';', '|', '&').TrimEnd(';', '|', '&');
    }

}
