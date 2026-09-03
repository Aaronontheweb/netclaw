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
        string temporaryDirectoryRoot,
        string worktreeDirectory,
        string logPath,
        IReadOnlyList<string> currentSessionRoots,
        string? legacyLogsBasePath)
    {
        Binding = binding;
        SessionDirectory = NormalizeAbsolute(sessionDirectory, nameof(sessionDirectory));
        AttachmentStagingDirectory = NormalizeAbsolute(
            attachmentStagingDirectory,
            nameof(attachmentStagingDirectory));
        ArtifactDirectory = NormalizeAbsolute(artifactDirectory, nameof(artifactDirectory));
        TemporaryDirectory = NormalizeAbsolute(temporaryDirectory, nameof(temporaryDirectory));
        TemporaryDirectoryRoot = NormalizeAbsolute(temporaryDirectoryRoot, nameof(temporaryDirectoryRoot));
        WorktreeDirectory = NormalizeAbsolute(worktreeDirectory, nameof(worktreeDirectory));
        LogPath = NormalizeAbsolute(logPath, nameof(logPath));
        CurrentSessionRoots = currentSessionRoots
            .Select(root => NormalizeAbsolute(root, nameof(currentSessionRoots)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        LegacyLogsBasePath = legacyLogsBasePath;
    }

    public SessionStorageBinding? Binding { get; }
    public string SessionDirectory { get; }
    public string AttachmentStagingDirectory { get; }
    public string ArtifactDirectory { get; }
    public string TemporaryDirectory { get; }
    public string TemporaryDirectoryRoot { get; }
    public string WorktreeDirectory { get; }
    public string LogPath { get; }
    public IReadOnlyList<string> CurrentSessionRoots { get; }
    private string? LegacyLogsBasePath { get; }

    public static SessionStoragePaths CreateVersion2(SessionStorageEnvelopeRoot envelopeRoot)
    {
        var root = envelopeRoot.Value;
        return new SessionStoragePaths(
            new SessionStorageBinding(SessionStorageLayoutVersion.Version2, envelopeRoot),
            Path.Combine(root, "workspace"),
            Path.Combine(root, "attachment-staging"),
            Path.Combine(root, "artifacts"),
            Path.Combine(root, "tmp", "parent"),
            root,
            Path.Combine(root, "worktrees"),
            Path.Combine(root, "logs", "session.log"),
            [root],
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
            normalizedSessionDirectory,
            Path.Combine(normalizedSessionDirectory, "worktrees"),
            Path.Combine(normalizedLogsBase, sanitizedSessionId, "session.log"),
            [normalizedSessionDirectory, normalizedLogsBase],
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
                binding.EnvelopeRoot.Value,
                WorktreeDirectory,
                Path.Combine(childRoot, "logs", "session.log"),
                CurrentSessionRoots,
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
            SessionDirectory,
            WorktreeDirectory,
            Path.Combine(
                LegacyLogsBasePath
                ?? throw new InvalidOperationException("Legacy storage is missing its log base."),
                sanitizedScopeId,
                "session.log"),
            CurrentSessionRoots,
            LegacyLogsBasePath);
    }

    private static string NormalizeAbsolute(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The path must be absolute.", parameterName);
        return Path.GetFullPath(path);
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
