// -----------------------------------------------------------------------
// <copyright file="SessionStoragePaths.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// A persisted session-storage layout version.
/// </summary>
public readonly record struct SessionStorageLayoutVersion
{
    public static SessionStorageLayoutVersion Version2 { get; } = new(2);

    public SessionStorageLayoutVersion(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The canonical absolute root for one versioned session storage envelope.
/// </summary>
public readonly record struct SessionStorageEnvelopeRoot
{
    public SessionStorageEnvelopeRoot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException("A session storage envelope root must be an absolute path.", nameof(value));

        string canonical;
        try
        {
            canonical = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("A session storage envelope root must be a valid path.", nameof(value), ex);
        }

        if (!string.Equals(
                value,
                canonical,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("A session storage envelope root must be canonical.", nameof(value));

        Value = canonical;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// The immutable durable binding for a versioned session storage envelope.
/// </summary>
public sealed record SessionStorageBinding(
    SessionStorageLayoutVersion LayoutVersion,
    SessionStorageEnvelopeRoot EnvelopeRoot);

/// <summary>
/// Data that lets file policy recognize same-session log paths without adding
/// the complete envelope or child root as a general file root.
/// </summary>
public sealed record SessionLogReadScope
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private SessionLogReadScope(
        SessionStorageEnvelopeRoot? envelopeRoot,
        string? legacyLogsBasePath,
        string? legacySessionPathPrefix)
    {
        EnvelopeRoot = envelopeRoot;
        LegacyLogsBasePath = legacyLogsBasePath;
        LegacySessionPathPrefix = legacySessionPathPrefix;
    }

    public SessionStorageEnvelopeRoot? EnvelopeRoot { get; }
    public string? LegacyLogsBasePath { get; }
    public string? LegacySessionPathPrefix { get; }

    internal static SessionLogReadScope Version2(SessionStorageEnvelopeRoot root)
        => new(root, null, null);

    internal static SessionLogReadScope Legacy(string logsBasePath, string sessionPathPrefix)
        => new(null, NormalizeAbsolute(logsBasePath, nameof(logsBasePath)), sessionPathPrefix);

    public bool TryGetReadRoot(string path, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        var canonical = Path.GetFullPath(path);
        if (EnvelopeRoot is { } envelope)
            return TryGetVersion2ReadRoot(envelope.Value, canonical, out root);

        return TryGetLegacyReadRoot(canonical, out root);
    }

    public bool IsSessionLogPath(string path, out bool belongsToCurrentSession)
    {
        belongsToCurrentSession = false;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        var canonical = Path.GetFullPath(path);
        if (TryGetReadRoot(canonical, out _))
        {
            belongsToCurrentSession = true;
            return true;
        }

        if (EnvelopeRoot is { } envelope)
        {
            var sessionsRoot = Directory.GetParent(envelope.Value)?.FullName;
            return sessionsRoot is not null && HasVersion2LogShape(sessionsRoot, canonical);
        }

        return LegacyLogsBasePath is { } logsBase && HasLegacyLogShape(logsBase, canonical);
    }

    private static bool TryGetVersion2ReadRoot(string envelope, string path, out string root)
    {
        var relative = Path.GetRelativePath(envelope, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 1 && string.Equals(segments[0], "logs", PathComparison))
        {
            root = Path.Combine(envelope, "logs");
            return true;
        }

        if (segments.Length >= 3
            && string.Equals(segments[0], "subagents", PathComparison)
            && !string.IsNullOrWhiteSpace(segments[1])
            && segments[1] is not "." and not ".."
            && string.Equals(segments[2], "logs", PathComparison))
        {
            root = Path.Combine(envelope, "subagents", segments[1], "logs");
            return true;
        }

        root = string.Empty;
        return false;
    }

    private bool TryGetLegacyReadRoot(string path, out string root)
    {
        var logsBase = LegacyLogsBasePath;
        var prefix = LegacySessionPathPrefix;
        if (logsBase is null || prefix is null)
        {
            root = string.Empty;
            return false;
        }

        var relative = Path.GetRelativePath(logsBase, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 1
            || segments[0] is "." or ".."
            || (!string.Equals(segments[0], prefix, PathComparison)
                && !segments[0].StartsWith(prefix + "_subagent_", PathComparison)))
        {
            root = string.Empty;
            return false;
        }

        root = Path.Combine(logsBase, segments[0]);
        return true;
    }

    private static bool HasVersion2LogShape(string sessionsRoot, string path)
    {
        var segments = GetRelativeSegments(sessionsRoot, path);
        return segments.Length >= 2
               && segments[0] is not "." and not ".."
               && (string.Equals(segments[1], "logs", PathComparison)
                   || (segments.Length >= 4
                       && string.Equals(segments[1], "subagents", PathComparison)
                       && segments[2] is not "." and not ".."
                       && string.Equals(segments[3], "logs", PathComparison)));
    }

    private static bool HasLegacyLogShape(string logsBase, string path)
    {
        var segments = GetRelativeSegments(logsBase, path);
        return segments.Length >= 2 && segments[0] is not "." and not "..";
    }

    private static string[] GetRelativeSegments(string root, string path)
        => Path.GetRelativePath(root, path).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizeAbsolute(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The path must be absolute.", parameterName);
        return Path.GetFullPath(path);
    }
}

/// <summary>
/// Immutable paths for one parent or child run.
/// </summary>
public sealed record SessionStoragePaths
{
    private SessionStoragePaths(
        SessionStorageBinding? binding,
        string sessionDirectory,
        string attachmentStagingDirectory,
        string artifactDirectory,
        string temporaryDirectory,
        string worktreeDirectory,
        string logPath,
        SessionLogReadScope logReadScope,
        string? legacyLogsBasePath)
    {
        Binding = binding;
        SessionDirectory = NormalizeAbsolute(sessionDirectory, nameof(sessionDirectory));
        AttachmentStagingDirectory = NormalizeAbsolute(
            attachmentStagingDirectory,
            nameof(attachmentStagingDirectory));
        ArtifactDirectory = NormalizeAbsolute(artifactDirectory, nameof(artifactDirectory));
        TemporaryDirectory = NormalizeAbsolute(temporaryDirectory, nameof(temporaryDirectory));
        WorktreeDirectory = NormalizeAbsolute(worktreeDirectory, nameof(worktreeDirectory));
        LogPath = NormalizeAbsolute(logPath, nameof(logPath));
        LogReadScope = logReadScope ?? throw new ArgumentNullException(nameof(logReadScope));
        LegacyLogsBasePath = legacyLogsBasePath;
    }

    public SessionStorageBinding? Binding { get; }
    public string SessionDirectory { get; }
    public string AttachmentStagingDirectory { get; }
    public string ArtifactDirectory { get; }
    public string TemporaryDirectory { get; }
    public string WorktreeDirectory { get; }
    public string LogPath { get; }
    public SessionLogReadScope LogReadScope { get; }
    private string? LegacyLogsBasePath { get; }

    public bool TryGetManagedDataRoot(string path, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        var canonical = Path.GetFullPath(path);
        if (IsWithin(canonical, TemporaryDirectory))
        {
            root = TemporaryDirectory;
            return true;
        }

        if (IsWithin(canonical, ArtifactDirectory))
        {
            root = ArtifactDirectory;
            return true;
        }

        var childRoot = Binding is { } binding
            ? Path.Combine(binding.EnvelopeRoot.Value, "subagents")
            : Path.Combine(SessionDirectory, "subagents");
        var segments = Path.GetRelativePath(childRoot, canonical).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2
            && segments[0] is not "." and not ".."
            && string.Equals(
                segments[1],
                "artifacts",
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            root = Path.Combine(childRoot, segments[0], "artifacts");
            return true;
        }

        return false;
    }

    public bool IsRestrictedEnvelopePath(string path)
    {
        if (Binding is not { } binding
            || string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var canonical = Path.GetFullPath(path);
        return IsWithin(canonical, binding.EnvelopeRoot.Value)
               && !IsWithin(canonical, SessionDirectory);
    }

    public static SessionStoragePaths CreateVersion2(SessionStorageEnvelopeRoot envelopeRoot)
    {
        var root = envelopeRoot.Value;
        return new SessionStoragePaths(
            new SessionStorageBinding(SessionStorageLayoutVersion.Version2, envelopeRoot),
            Path.Combine(root, "workspace"),
            Path.Combine(root, "attachment-staging"),
            Path.Combine(root, "artifacts"),
            Path.Combine(root, "tmp", "parent"),
            Path.Combine(root, "worktrees"),
            Path.Combine(root, "logs", "session.log"),
            SessionLogReadScope.Version2(envelopeRoot),
            null);
    }

    public static SessionStoragePaths CreateLegacy(
        string sessionDirectory,
        string sessionLogsBasePath,
        string sanitizedSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedSessionId);
        var normalizedSessionDirectory = NormalizeAbsolute(sessionDirectory, nameof(sessionDirectory));
        var normalizedLogsBase = NormalizeAbsolute(sessionLogsBasePath, nameof(sessionLogsBasePath));
        return new SessionStoragePaths(
            null,
            normalizedSessionDirectory,
            Path.Combine(
                Directory.GetParent(normalizedSessionDirectory)?.FullName
                ?? throw new ArgumentException("The legacy session directory needs a parent.", nameof(sessionDirectory)),
                ".attachment-staging",
                sanitizedSessionId),
            Path.Combine(normalizedSessionDirectory, "artifacts"),
            Path.Combine(normalizedSessionDirectory, "tmp", "parent"),
            Path.Combine(normalizedSessionDirectory, "worktrees"),
            Path.Combine(normalizedLogsBase, sanitizedSessionId, "session.log"),
            SessionLogReadScope.Legacy(normalizedLogsBase, sanitizedSessionId),
            normalizedLogsBase);
    }

    public SessionStoragePaths ForChild(SubAgentRunId runId, SubAgentScopeId legacyScopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyScopeId.Value);

        if (Binding is { } binding)
        {
            var childRoot = Path.Combine(binding.EnvelopeRoot.Value, "subagents", runId.Value);
            return new SessionStoragePaths(
                binding,
                SessionDirectory,
                AttachmentStagingDirectory,
                Path.Combine(childRoot, "artifacts"),
                Path.Combine(childRoot, "tmp"),
                WorktreeDirectory,
                Path.Combine(childRoot, "logs", "session.log"),
                LogReadScope,
                null);
        }

        var childRootLegacy = Path.Combine(SessionDirectory, "subagents", runId.Value);
        var sanitizedScopeId = SanitizePathSegment(legacyScopeId.Value);
        return new SessionStoragePaths(
            null,
            SessionDirectory,
            AttachmentStagingDirectory,
            Path.Combine(childRootLegacy, "artifacts"),
            Path.Combine(childRootLegacy, "tmp"),
            WorktreeDirectory,
            Path.Combine(
                LegacyLogsBasePath
                ?? throw new InvalidOperationException("Legacy storage is missing its log base."),
                sanitizedScopeId,
                "session.log"),
            LogReadScope,
            LegacyLogsBasePath);
    }

    private static string NormalizeAbsolute(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The path must be absolute.", parameterName);
        return Path.GetFullPath(path);
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".."
               && !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static string SanitizePathSegment(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        for (var index = 0; index < value.Length; index++)
        {
            buffer[index] = char.IsLetterOrDigit(value[index]) || value[index] == '-'
                ? value[index]
                : '_';
        }

        return new string(buffer);
    }
}
