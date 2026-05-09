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
    /// work. Returns an empty list when the command is "messy" — i.e., contains
    /// bash control-flow keywords (<c>for</c>/<c>while</c>/<c>do</c>/<c>done</c>/
    /// <c>then</c>/<c>fi</c>/<c>case</c>/<c>esac</c>) or unbalanced
    /// quotes/brackets — so the approval gate can offer only one-shot approval
    /// without persisting fragmentary verb chains derived from control-flow
    /// tokens. See <see cref="IsMessyCompoundCommand"/> for the predicate.
    /// </summary>
    public static IReadOnlyList<string> SplitCompoundCommand(string command)
    {
        if (IsMessyCompoundCommand(command))
            return [];
        return ShellApprovalSemantics.ForCommand(command).SplitCompoundCommand(command);
    }

    private static readonly HashSet<string> BashControlFlowKeywords = new(StringComparer.Ordinal)
    {
        "for", "while", "do", "done", "then", "fi", "case", "esac"
    };

    /// <summary>
    /// Returns true when <paramref name="command"/> contains bash control-flow
    /// keywords as unquoted standalone words, or has unbalanced quotes,
    /// parentheses, brackets, or braces. Used by the approval gate to refuse
    /// persistent grants for commands that cannot be cleanly split into verb
    /// chains the operator could later reason about.
    ///
    /// Cheap structural scan; not a bash parser. Heredocs, here-strings, and
    /// command substitution are not analyzed semantically — only their
    /// quote/bracket balance contributes.
    /// </summary>
    public static bool IsMessyCompoundCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        char? quote = null;
        var parens = 0;
        var brackets = 0;
        var braces = 0;
        var word = new StringBuilder();

        foreach (var ch in command)
        {
            // Quote handling: opens skip the bracket scan, closes resume it.
            if (quote is null && (ch == '\'' || ch == '"'))
            {
                if (FlushWordAsKeyword(word)) return true;
                quote = ch;
                continue;
            }

            if (quote == ch)
            {
                quote = null;
                continue;
            }

            if (quote is not null)
                continue;

            switch (ch)
            {
                case '(':
                    parens++;
                    if (FlushWordAsKeyword(word)) return true;
                    continue;
                case ')':
                    if (--parens < 0) return true;
                    if (FlushWordAsKeyword(word)) return true;
                    continue;
                case '[':
                    brackets++;
                    if (FlushWordAsKeyword(word)) return true;
                    continue;
                case ']':
                    if (--brackets < 0) return true;
                    if (FlushWordAsKeyword(word)) return true;
                    continue;
                case '{':
                    braces++;
                    if (FlushWordAsKeyword(word)) return true;
                    continue;
                case '}':
                    if (--braces < 0) return true;
                    if (FlushWordAsKeyword(word)) return true;
                    continue;
            }

            if (char.IsWhiteSpace(ch) || ch is ';' or '|' or '&')
            {
                if (FlushWordAsKeyword(word)) return true;
                continue;
            }

            word.Append(ch);
        }

        if (FlushWordAsKeyword(word)) return true;

        return quote is not null || parens != 0 || brackets != 0 || braces != 0;
    }

    private static bool FlushWordAsKeyword(StringBuilder word)
    {
        if (word.Length == 0)
            return false;

        var match = BashControlFlowKeywords.Contains(word.ToString());
        word.Clear();
        return match;
    }

    /// <summary>
    /// Extracts the verb chain (command name + subcommands) from a tokenized
    /// command. Stops at the first token that looks like a flag (starts with -)
    /// or an argument (path, URL, etc.), and caps at <paramref name="maxDepth"/>
    /// tokens (default: 2) to avoid capturing positional arguments as subcommands.
    /// The verb chain SHALL NOT include any path-like tokens — paths are
    /// extracted separately via <see cref="ExtractFirstPathArgument"/> and
    /// become the candidate's effective directory in the approval matcher.
    /// </summary>
    public static string ExtractVerbChain(string command, int maxDepth = 2)
        => ShellApprovalSemantics.ForCommand(command).ExtractVerbChain(command, maxDepth);

    /// <summary>
    /// Returns the first path-like positional argument from a single shell
    /// clause, or null when no path token is present. Used by the approval
    /// gate to determine the candidate's effective directory: when present,
    /// it overrides the resolved cwd; when absent, the matcher falls back
    /// to cwd.
    /// </summary>
    /// <remarks>
    /// File-targeting commands (e.g. <c>cat ~/.bashrc</c>) return the parent
    /// directory of the extracted path so persisted approvals scope to a
    /// folder rather than a single file. This is a deterministic string
    /// operation — no filesystem syscall is performed at extract time.
    /// </remarks>
    public static string? ExtractFirstPathArgument(string command)
        => ShellApprovalSemantics.ForCommand(command).ExtractFirstPathArgument(command);

    /// <summary>
    /// Conservative path-token classifier. Returns true when the token
    /// starts with <c>/</c>, <c>~/</c>, <c>./</c>, <c>../</c>, or is exactly
    /// <c>~</c>, <c>.</c>, or <c>..</c>. Tokens that merely contain a slash
    /// internally (URLs, regexes, docker tags, git refs) are NOT classified
    /// as paths — false positives here would silently expand or contract
    /// the approval gate's trust scope.
    /// </summary>
    public static bool IsPathToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        if (token == "~" || token == "." || token == "..")
            return true;

        return token.StartsWith('/')
            || token.StartsWith("~/", StringComparison.Ordinal)
            || token.StartsWith("./", StringComparison.Ordinal)
            || token.StartsWith("../", StringComparison.Ordinal);
    }

    /// <summary>
    /// Produces an exact shell approval unit string with recognizable local paths
    /// normalized against the working directory. Non-path tokens remain in order.
    /// </summary>
    public static string NormalizeApprovalUnit(string command, string? workingDirectory = null)
        => ShellApprovalSemantics.ForCommand(command).NormalizeApprovalUnit(command, workingDirectory);

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
