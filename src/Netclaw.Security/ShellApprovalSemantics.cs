// -----------------------------------------------------------------------
// <copyright file="ShellApprovalSemantics.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Security;

internal interface IShellApprovalSemantics
{
    IReadOnlyList<string> SplitCompoundCommand(string command);

    string ExtractVerbChain(string command, int maxDepth);

    IReadOnlyList<string> ExtractInnerCommands(string command);

    bool LooksLikePath(string token);

    string NormalizeApprovalUnit(string command, string? workingDirectory);

    IReadOnlyList<DirectoryApprovalRoot> ExtractDirectoryRoots(string command, string? workingDirectory);

    string? NormalizePathToken(string path, string? workingDirectory);
}

internal static class ShellApprovalSemantics
{
    private static readonly IShellApprovalSemantics Posix = PosixShellApprovalSemantics.Instance;
    private static readonly IShellApprovalSemantics Windows = WindowsShellApprovalSemantics.Instance;

    public static IShellApprovalSemantics Current { get; } = OperatingSystem.IsWindows()
        ? Windows
        : Posix;

    public static IShellApprovalSemantics ForCommand(string? command)
    {
        if (!OperatingSystem.IsWindows())
            return Posix;

        if (string.IsNullOrWhiteSpace(command))
            return Windows;

        var tokens = ShellTokenizer.Tokenize(command).ToList();
        if (tokens.Count == 0)
            return Windows;

        var first = ShellTokenizer.TrimShellPunctuation(tokens[0]);
        if (PosixShellApprovalSemantics.IsPosixShellInvoker(first))
            return Posix;

        if (WindowsShellApprovalSemantics.IsWindowsShellInvoker(first))
            return Windows;

        foreach (var token in tokens)
        {
            var trimmed = ShellTokenizer.TrimShellPunctuation(token);
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('-'))
                continue;

            if (LooksLikePosixCommandPath(trimmed))
                return Posix;

            if (Windows.LooksLikePath(trimmed))
                return Windows;
        }

        return Windows;
    }

    private static bool LooksLikePosixCommandPath(string token)
    {
        if (token.Contains("://", StringComparison.Ordinal))
            return false;

        if (token.Equals("/c", StringComparison.OrdinalIgnoreCase)
            || token.Equals("/k", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Posix.LooksLikePath(token);
    }
}

internal abstract class ShellApprovalSemanticsBase : IShellApprovalSemantics
{
    public abstract IReadOnlyList<string> SplitCompoundCommand(string command);

    public abstract IReadOnlyList<string> ExtractInnerCommands(string command);

    public abstract bool LooksLikePath(string token);

    protected abstract bool IsShellSeparator(char ch);

    protected abstract bool IsAnchoredPath(string token);

    public string ExtractVerbChain(string command, int maxDepth)
    {
        var tokens = ShellTokenizer.Tokenize(command).ToList();
        if (tokens.Count == 0)
            return string.Empty;

        var verbParts = new List<string>();
        foreach (var token in tokens)
        {
            if (verbParts.Count >= maxDepth)
                break;

            var trimmed = ShellTokenizer.TrimShellPunctuation(token);
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith('-'))
                break;

            if (LooksLikeArgument(trimmed))
                break;

            verbParts.Add(trimmed);
        }

        if (verbParts.Count == 1 && ShellTokenizer.PathAwareVerbs.Contains(verbParts[0]))
        {
            for (var i = 1; i < tokens.Count; i++)
            {
                var trimmed = ShellTokenizer.TrimShellPunctuation(tokens[i]);
                if (trimmed.Length == 0)
                    continue;

                if (trimmed.StartsWith('-'))
                    continue;

                verbParts.Add(trimmed);
                break;
            }
        }

        return string.Join(' ', verbParts);
    }

    public string NormalizeApprovalUnit(string command, string? workingDirectory)
    {
        var tokens = ShellTokenizer.Tokenize(command).ToList();
        if (tokens.Count == 0)
            return string.Empty;

        var normalizedTokens = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!LooksLikePath(token))
            {
                normalizedTokens.Add(token);
                continue;
            }

            var normalized = NormalizePathToken(token, workingDirectory);
            normalizedTokens.Add(normalized ?? token);
        }

        return string.Join(' ', normalizedTokens);
    }

    public IReadOnlyList<DirectoryApprovalRoot> ExtractDirectoryRoots(string command, string? workingDirectory)
    {
        var tokens = ShellTokenizer.Tokenize(command).ToList();
        if (tokens.Count == 0)
            return [];

        var roots = new List<DirectoryApprovalRoot>();
        var comparisonRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawPathToken = false;

        foreach (var token in tokens)
        {
            if (token.Length == 0 || token.StartsWith('-'))
                continue;

            if (!LooksLikePath(token))
                continue;

            sawPathToken = true;
            var root = TryCreateDirectoryApprovalRoot(token, workingDirectory);
            if (root is null)
                return [];

            if (comparisonRoots.Add(root.ComparisonRoot))
                roots.Add(root);
        }

        return sawPathToken ? roots : [];
    }

    public virtual string? NormalizePathToken(string path, string? workingDirectory)
        => PathUtility.ExpandAndNormalize(path, workingDirectory);

    protected virtual bool LooksLikeArgument(string token)
    {
        return ContainsShellPathSeparator(token)
            || token.StartsWith('~')
            || token.StartsWith('.')
            || token.Contains("://", StringComparison.Ordinal)
            || token.Contains(':', StringComparison.Ordinal)
            || token.StartsWith('$')
            || token.StartsWith('%')
            || token.Contains('*', StringComparison.Ordinal);
    }

    protected bool ContainsShellPathSeparator(string token)
    {
        foreach (var ch in token)
        {
            if (IsShellSeparator(ch))
                return true;
        }

        return false;
    }

    protected bool HasTraversalComponent(string token)
    {
        return token.Contains("/../", StringComparison.Ordinal)
            || token.EndsWith("/..", StringComparison.Ordinal)
            || token.Contains("\\..\\", StringComparison.Ordinal)
            || token.EndsWith("\\..", StringComparison.Ordinal);
    }

    protected static bool HasFileExtensionInLastComponent(string token)
    {
        var lastComponent = Path.GetFileName(token);
        if (string.IsNullOrWhiteSpace(lastComponent))
            return false;

        var ext = Path.GetExtension(lastComponent);
        return ext.Length > 1;
    }

    protected int GetFirstShellSeparatorIndex(string token)
    {
        for (var i = 0; i < token.Length; i++)
        {
            if (IsShellSeparator(token[i]))
                return i;
        }

        return -1;
    }

    protected int GetLastShellSeparatorIndex(string token, int startExclusive)
    {
        for (var i = Math.Min(startExclusive - 1, token.Length - 1); i >= 0; i--)
        {
            if (IsShellSeparator(token[i]))
                return i;
        }

        return -1;
    }

    protected static IReadOnlyList<string> SplitCompoundCommand(string command, bool splitOnSemicolon, bool splitOnSingleAmpersand)
    {
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var segments = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var span = command.AsSpan();

        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];

            if (quote is null && (ch == '\'' || ch == '"'))
            {
                quote = ch;
                current.Append(ch);
                continue;
            }

            if (quote is not null && ch == quote)
            {
                quote = null;
                current.Append(ch);
                continue;
            }

            if (quote is not null)
            {
                current.Append(ch);
                continue;
            }

            if (i + 1 < span.Length)
            {
                var twoChar = span.Slice(i, 2);
                if (twoChar is "&&" or "||")
                {
                    FlushSegment(current, segments);
                    i++;
                    continue;
                }
            }

            if (splitOnSingleAmpersand && ch == '&')
            {
                FlushSegment(current, segments);
                continue;
            }

            if (splitOnSemicolon && ch == ';')
            {
                FlushSegment(current, segments);
                continue;
            }

            current.Append(ch);
        }

        FlushSegment(current, segments);
        return segments;
    }

    protected DirectoryApprovalRoot? TryCreateDirectoryApprovalRoot(string rawPath, string? workingDirectory)
    {
        var displayRoot = ExtractDisplayDirectory(rawPath, workingDirectory);
        if (displayRoot is null)
            return null;

        var comparisonRoot = NormalizePathToken(displayRoot, workingDirectory);
        if (comparisonRoot is null)
            return null;

        if (Directory.Exists(comparisonRoot))
            comparisonRoot = PathUtility.Normalize(new DirectoryInfo(comparisonRoot).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? comparisonRoot);

        if (CountPathSegments(comparisonRoot) < ShellTokenizer.MinDirectoryScopeDepth)
            return null;

        return new DirectoryApprovalRoot(EnsureTrailingSeparator(displayRoot), EnsureTrailingSeparator(comparisonRoot));
    }

    protected virtual string? ExtractDisplayDirectory(string path, string? workingDirectory)
    {
        string? candidate;
        if (path.EndsWith('/') || path.EndsWith('\\'))
        {
            candidate = path.TrimEnd('/', '\\');
        }
        else
        {
            var globIdx = path.IndexOfAny(['*', '?', '[']);
            if (globIdx >= 0)
            {
                var lastSep = GetLastShellSeparatorIndex(path, globIdx);
                candidate = lastSep > 0 ? path[..lastSep] : null;
            }
            else
            {
                var normalizedCandidate = NormalizePathToken(path, workingDirectory);
                if (normalizedCandidate is not null && Directory.Exists(normalizedCandidate))
                    candidate = path;
                else
                    candidate = Path.GetDirectoryName(path);
            }
        }

        return NormalizeDisplayDirectory(candidate, workingDirectory);
    }

    protected virtual string? NormalizeDisplayDirectory(string? candidate, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var trimmed = Path.TrimEndingDirectorySeparator(candidate);
        if (IsRelativeDisplayPath(trimmed))
            return trimmed;

        return NormalizePathToken(trimmed, workingDirectory);
    }

    protected virtual bool IsRelativeDisplayPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith('~')
            || path.StartsWith("$HOME", StringComparison.Ordinal)
            || path.StartsWith("${HOME}", StringComparison.Ordinal)
            || path.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !Path.IsPathRooted(path);
    }

    protected virtual string EnsureTrailingSeparator(string path)
        => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    protected virtual int CountPathSegments(string normalizedPath)
    {
        var trimmed = normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
            return 0;

        var root = Path.GetPathRoot(trimmed);
        if (root is not null && trimmed.Length > root.Length)
            trimmed = trimmed[root.Length..];
        else if (root is not null)
            return 0;

        return trimmed.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static void FlushSegment(StringBuilder current, List<string> segments)
    {
        var trimmed = current.ToString().Trim();
        if (trimmed.Length > 0)
            segments.Add(trimmed);

        current.Clear();
    }
}

internal sealed class PosixShellApprovalSemantics : ShellApprovalSemanticsBase
{
    public static readonly PosixShellApprovalSemantics Instance = new();

    public override IReadOnlyList<string> SplitCompoundCommand(string command)
        => SplitCompoundCommand(command, splitOnSemicolon: true, splitOnSingleAmpersand: false);

    public override IReadOnlyList<string> ExtractInnerCommands(string command)
    {
        var tokens = ShellTokenizer.Tokenize(command).ToList();
        var results = new List<string>();

        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var verb = ShellTokenizer.TrimShellPunctuation(tokens[i]);
            if (!IsPosixShellInvoker(verb))
                continue;

            if (i + 1 < tokens.Count && IsShellCommandFlag(tokens[i + 1]) && i + 2 < tokens.Count)
                results.Add(tokens[i + 2]);
        }

        return results;
    }

    public override bool LooksLikePath(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.StartsWith('-'))
            return false;

        if (token.Contains("://", StringComparison.Ordinal))
            return false;

        if (IsAnchoredPath(token))
            return true;

        if (!ContainsShellPathSeparator(token))
            return false;

        var firstSlash = GetFirstShellSeparatorIndex(token);
        if (token.IndexOf(':', StringComparison.Ordinal) is var colonIdx && colonIdx >= 0 && colonIdx < firstSlash)
            return false;

        if (token.StartsWith('@') && token.IndexOf('/', 1) == token.LastIndexOf('/'))
            return false;

        if ((token.StartsWith("s/", StringComparison.Ordinal) || token.StartsWith("y/", StringComparison.Ordinal))
            && CountChar(token, '/') >= 3)
        {
            return false;
        }

        return HasTraversalComponent(token) || HasFileExtensionInLastComponent(token);
    }

    public override string? NormalizePathToken(string path, string? workingDirectory)
    {
        var expanded = PathUtility.ExpandHome(path);

        if (LooksLikePosixAbsoluteShellPath(expanded))
            return NormalizePosixShellPath(expanded);

        return PathUtility.ExpandAndNormalize(expanded, workingDirectory);
    }

    protected override string? ExtractDisplayDirectory(string path, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (path.EndsWith('/') || path.EndsWith('\\'))
            return path.TrimEnd('/', '\\');

        var globIdx = path.IndexOfAny(['*', '?', '[']);
        if (globIdx >= 0)
        {
            var lastSep = path.LastIndexOf('/', globIdx);
            return lastSep > 0 ? path[..lastSep] : null;
        }

        var normalizedCandidate = NormalizePathToken(path, workingDirectory);
        if (normalizedCandidate is not null && Directory.Exists(normalizedCandidate))
            return path;

        var lastSlash = path.LastIndexOf('/');
        return lastSlash > 0 ? path[..lastSlash] : null;
    }

    protected override bool IsShellSeparator(char ch) => ch == '/';

    protected override bool IsAnchoredPath(string token)
    {
        return Path.IsPathRooted(token)
            || token.StartsWith("./", StringComparison.Ordinal)
            || token.StartsWith("../", StringComparison.Ordinal)
            || token.StartsWith('~')
            || token.StartsWith("$HOME", StringComparison.Ordinal)
            || token.StartsWith("${HOME}", StringComparison.Ordinal);
    }

    internal static bool IsPosixShellInvoker(string verb)
    {
        return verb is "bash" or "sh" or "/bin/bash" or "/bin/sh"
            or "/usr/bin/bash" or "/usr/bin/sh" or "zsh" or "/bin/zsh";
    }

    private static bool LooksLikePosixAbsoluteShellPath(string path)
    {
        return path.Length > 0 && path[0] == '/'
            && !path.StartsWith("//", StringComparison.Ordinal)
            && path.IndexOf('\\', StringComparison.Ordinal) < 0
            && !path.Contains("://", StringComparison.Ordinal);
    }

    private static string NormalizePosixShellPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (normalized.Count > 0)
                    normalized.RemoveAt(normalized.Count - 1);

                continue;
            }

            normalized.Add(segment);
        }

        return normalized.Count == 0 ? "/" : "/" + string.Join('/', normalized);
    }

    private static bool IsShellCommandFlag(string token)
    {
        if (token.Length == 0 || token[0] != '-' || token.StartsWith("--", StringComparison.Ordinal))
            return false;

        return token.AsSpan(1).IndexOf('c') >= 0;
    }

    private static int CountChar(string value, char target)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (c == target)
                count++;
        }

        return count;
    }
}

internal sealed class WindowsShellApprovalSemantics : ShellApprovalSemanticsBase
{
    public static readonly WindowsShellApprovalSemantics Instance = new();

    public override IReadOnlyList<string> SplitCompoundCommand(string command)
        // Windows approval splitting handles both cmd.exe control operators (`&`, `&&`, `||`)
        // and PowerShell's `;` because nested PowerShell invocations are common under `cmd /c`.
        => SplitCompoundCommand(command, splitOnSemicolon: true, splitOnSingleAmpersand: true);

    public override IReadOnlyList<string> ExtractInnerCommands(string command)
    {
        var tokens = ShellTokenizer.Tokenize(command).ToList();
        var results = new List<string>();

        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var verb = ShellTokenizer.TrimShellPunctuation(tokens[i]);
            if (IsCmdInvoker(verb))
            {
                if (i + 1 < tokens.Count && IsCmdCommandFlag(tokens[i + 1]) && i + 2 < tokens.Count)
                    results.Add(tokens[i + 2]);

                continue;
            }

            if (IsPowerShellInvoker(verb)
                && i + 1 < tokens.Count
                && IsPowerShellCommandFlag(tokens[i + 1])
                && i + 2 < tokens.Count)
            {
                results.Add(tokens[i + 2]);
            }
        }

        return results;
    }

    public override bool LooksLikePath(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.StartsWith('-'))
            return false;

        if (token.Contains("://", StringComparison.Ordinal))
            return false;

        if (IsAnchoredPath(token))
            return true;

        if (!ContainsShellPathSeparator(token))
            return false;

        var firstSeparator = GetFirstShellSeparatorIndex(token);
        if (token.IndexOf(':', StringComparison.Ordinal) is var colonIdx && colonIdx >= 0 && colonIdx < firstSeparator)
        {
            var isDrivePrefix = colonIdx == 1 && char.IsAsciiLetter(token[0]);
            if (!isDrivePrefix)
                return false;
        }

        if (token.StartsWith('@') && token.IndexOf('/', 1) == token.LastIndexOf('/'))
            return false;

        if ((token.StartsWith("s/", StringComparison.Ordinal) || token.StartsWith("y/", StringComparison.Ordinal))
            && CountChar(token, '/') >= 3)
        {
            return false;
        }

        if (token.Contains('\\', StringComparison.Ordinal))
            return true;

        return HasTraversalComponent(token) || HasFileExtensionInLastComponent(token);
    }

    protected override bool IsShellSeparator(char ch) => ch is '/' or '\\';

    protected override bool IsAnchoredPath(string token)
    {
        return IsWindowsRootedPath(token)
            || token.StartsWith("./", StringComparison.Ordinal)
            || token.StartsWith("../", StringComparison.Ordinal)
            || token.StartsWith(@".\", StringComparison.Ordinal)
            || token.StartsWith(@"..\", StringComparison.Ordinal)
            || token.StartsWith('~')
            || token.StartsWith("$HOME", StringComparison.Ordinal)
            || token.StartsWith("${HOME}", StringComparison.Ordinal)
            || token.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    public override string? NormalizePathToken(string path, string? workingDirectory)
    {
        var expanded = PathUtility.ExpandHome(path);

        return PathUtility.ExpandAndNormalize(expanded, workingDirectory);
    }

    internal static bool IsWindowsShellInvoker(string verb)
    {
        return IsCmdInvoker(verb) || IsPowerShellInvoker(verb);
    }

    private static bool IsCmdInvoker(string verb)
        => verb.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsRootedPath(string token)
    {
        if (token.StartsWith("\\\\", StringComparison.Ordinal))
            return true;

        return token.Length >= 3
            && char.IsAsciiLetter(token[0])
            && token[1] == ':'
            && (token[2] == '\\' || token[2] == '/');
    }

    private static bool IsPowerShellInvoker(string verb)
    {
        return verb.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCmdCommandFlag(string token)
    {
        return token.Equals("/c", StringComparison.OrdinalIgnoreCase)
            || token.Equals("/k", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPowerShellCommandFlag(string token)
    {
        return token.Equals("-c", StringComparison.OrdinalIgnoreCase)
            || token.Equals("-command", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountChar(string value, char target)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (c == target)
                count++;
        }

        return count;
    }
}
