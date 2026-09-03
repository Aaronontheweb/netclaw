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
    /// <summary>The unified session-envelope layout.</summary>
    public static SessionStorageLayoutVersion Version2 { get; } = new(2);

    /// <summary>Creates a positive storage-layout version.</summary>
    /// <param name="value">The positive wire value.</param>
    public SessionStorageLayoutVersion(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    /// <summary>Gets the positive wire value.</summary>
    public int Value { get; }

    /// <summary>Returns the wire value.</summary>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The canonical absolute root for one versioned session storage envelope.
/// </summary>
public readonly record struct SessionStorageEnvelopeRoot
{
    /// <summary>Creates a canonical absolute envelope root.</summary>
    /// <param name="value">The canonical absolute directory path.</param>
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

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>
/// The immutable durable binding for a versioned session storage envelope.
/// </summary>
/// <param name="LayoutVersion">The layout that interprets the envelope.</param>
/// <param name="EnvelopeRoot">The immutable absolute envelope root.</param>
public sealed record SessionStorageBinding(
    SessionStorageLayoutVersion LayoutVersion,
    SessionStorageEnvelopeRoot EnvelopeRoot);

/// <summary>
/// The validated directory and storage root for one run's temporary files.
/// </summary>
public readonly record struct ManagedTemporaryLocation
{
    /// <summary>Creates a managed temporary location below its storage root.</summary>
    /// <param name="directory">The absolute temporary directory.</param>
    /// <param name="storageRoot">The absolute storage root that contains the directory.</param>
    public ManagedTemporaryLocation(string directory, string storageRoot)
    {
        Directory = new ManagedTemporaryDirectory(directory);
        StorageRoot = new ManagedTemporaryStorageRoot(storageRoot);

        var relative = Path.GetRelativePath(StorageRoot.Value, Directory.Value);
        if (relative == "."
            || Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The managed temporary directory must be inside its storage root.",
                nameof(directory));
        }
    }

    /// <summary>Gets the process-specific temporary directory.</summary>
    public ManagedTemporaryDirectory Directory { get; }

    /// <summary>Gets the storage root that contains <see cref="Directory"/>.</summary>
    public ManagedTemporaryStorageRoot StorageRoot { get; }
}

/// <summary>
/// Immutable paths for one parent or child run. The resolved paths define storage,
/// but they do not bypass content admission or filesystem authorization.
/// </summary>
public sealed record SessionStoragePaths
{
    private SessionStoragePaths(
        SessionStorageBinding? binding,
        SessionWorkspaceDirectory sessionDirectory,
        AttachmentStagingDirectory attachmentStagingDirectory,
        ArtifactDirectory artifactDirectory,
        ManagedTemporaryLocation managedTemporary,
        WorktreeDirectory worktreeDirectory,
        SessionLogPath logPath,
        LegacySessionLogsDirectory? legacyLogsBasePath)
    {
        Binding = binding;
        SessionDirectory = sessionDirectory;
        AttachmentStagingDirectory = attachmentStagingDirectory;
        ArtifactDirectory = artifactDirectory;
        ManagedTemporary = managedTemporary;
        WorktreeDirectory = worktreeDirectory;
        LogPath = logPath;
        LegacyLogsBasePath = legacyLogsBasePath;
    }

    /// <summary>Gets the durable versioned binding. A null value identifies an unchanged legacy layout.</summary>
    public SessionStorageBinding? Binding { get; }
    /// <summary>Gets the session workspace and default relative-path base.</summary>
    public SessionWorkspaceDirectory SessionDirectory { get; }
    /// <summary>Gets the directory for untrusted attachments before content admission.</summary>
    public AttachmentStagingDirectory AttachmentStagingDirectory { get; }
    /// <summary>Gets the current run's retained artifact directory.</summary>
    public ArtifactDirectory ArtifactDirectory { get; }
    /// <summary>Gets the current run's managed temporary location.</summary>
    public ManagedTemporaryLocation ManagedTemporary { get; }
    /// <summary>Gets the session-owned directory for Git worktrees.</summary>
    public WorktreeDirectory WorktreeDirectory { get; }
    /// <summary>Gets the current run's raw session log path.</summary>
    public SessionLogPath LogPath { get; }
    private LegacySessionLogsDirectory? LegacyLogsBasePath { get; }

    /// <summary>Creates the version-2 parent layout below one persisted envelope.</summary>
    /// <param name="envelopeRoot">The persisted envelope root.</param>
    /// <returns>The resolved parent paths.</returns>
    public static SessionStoragePaths CreateVersion2(SessionStorageEnvelopeRoot envelopeRoot)
    {
        var root = envelopeRoot.Value;
        return new SessionStoragePaths(
            new SessionStorageBinding(SessionStorageLayoutVersion.Version2, envelopeRoot),
            new SessionWorkspaceDirectory(Path.Combine(root, "workspace")),
            new AttachmentStagingDirectory(Path.Combine(root, "attachment-staging")),
            new ArtifactDirectory(Path.Combine(root, "artifacts")),
            new ManagedTemporaryLocation(Path.Combine(root, "tmp", "parent"), root),
            new WorktreeDirectory(Path.Combine(root, "worktrees")),
            new SessionLogPath(Path.Combine(root, "logs", "session.log")),
            null);
    }

    /// <summary>Creates paths for an existing session without a versioned binding.</summary>
    /// <param name="sessionDirectory">The established session directory.</param>
    /// <param name="sessionLogsBasePath">The established session-log base.</param>
    /// <param name="sanitizedSessionId">The legacy path segment for the session.</param>
    /// <returns>The unchanged legacy paths.</returns>
    public static SessionStoragePaths CreateLegacy(
        string sessionDirectory,
        string sessionLogsBasePath,
        string sanitizedSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedSessionId);
        var normalizedSessionDirectory = new SessionWorkspaceDirectory(sessionDirectory);
        var normalizedLogsBase = new LegacySessionLogsDirectory(sessionLogsBasePath);
        return new SessionStoragePaths(
            null,
            normalizedSessionDirectory,
            new AttachmentStagingDirectory(Path.Combine(
                Directory.GetParent(normalizedSessionDirectory.Value)?.FullName
                ?? throw new ArgumentException("The legacy session directory needs a parent.", nameof(sessionDirectory)),
                ".attachment-staging",
                sanitizedSessionId)),
            new ArtifactDirectory(Path.Combine(normalizedSessionDirectory.Value, "artifacts")),
            new ManagedTemporaryLocation(
                Path.Combine(normalizedSessionDirectory.Value, "tmp", "parent"),
                normalizedSessionDirectory.Value),
            new WorktreeDirectory(Path.Combine(normalizedSessionDirectory.Value, "worktrees")),
            new SessionLogPath(Path.Combine(normalizedLogsBase.Value, sanitizedSessionId, "session.log")),
            normalizedLogsBase);
    }

    /// <summary>Derives one child run from the parent layout.</summary>
    /// <param name="runId">The opaque child run identifier.</param>
    /// <param name="legacyScopeId">The legacy scope used only for an old log layout.</param>
    /// <returns>The child-specific artifact, temporary, and log paths.</returns>
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
                new ArtifactDirectory(Path.Combine(childRoot, "artifacts")),
                new ManagedTemporaryLocation(Path.Combine(childRoot, "tmp"), binding.EnvelopeRoot.Value),
                WorktreeDirectory,
                new SessionLogPath(Path.Combine(childRoot, "logs", "session.log")),
                null);
        }

        var childRootLegacy = Path.Combine(SessionDirectory.Value, "subagents", runId.Value);
        var sanitizedScopeId = SanitizePathSegment(legacyScopeId.Value);
        return new SessionStoragePaths(
            null,
            SessionDirectory,
            AttachmentStagingDirectory,
            new ArtifactDirectory(Path.Combine(childRootLegacy, "artifacts")),
            new ManagedTemporaryLocation(Path.Combine(childRootLegacy, "tmp"), SessionDirectory.Value),
            WorktreeDirectory,
            new SessionLogPath(Path.Combine(
                LegacyLogsBasePath?.Value
                ?? throw new InvalidOperationException("Legacy storage is missing its log base."),
                sanitizedScopeId,
                "session.log")),
            LegacyLogsBasePath);
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
