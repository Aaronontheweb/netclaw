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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
/// The validated directory and authority root for one run's temporary files.
/// </summary>
public readonly record struct ManagedTemporaryLocation
{
    /// <summary>Creates a managed temporary location below its authority root.</summary>
    /// <param name="directory">The absolute temporary directory.</param>
    /// <param name="authorityRoot">The absolute root that contains the directory.</param>
    public ManagedTemporaryLocation(string directory, string authorityRoot)
    {
        Directory = SessionStoragePaths.NormalizeAbsolute(directory, nameof(directory));
        AuthorityRoot = SessionStoragePaths.NormalizeAbsolute(authorityRoot, nameof(authorityRoot));

        var relative = Path.GetRelativePath(AuthorityRoot, Directory);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The managed temporary directory must be inside its authority root.",
                nameof(directory));
        }
    }

    /// <summary>Gets the process-specific temporary directory.</summary>
    public string Directory { get; }

    /// <summary>Gets the root that authorizes creation of <see cref="Directory"/>.</summary>
    public string AuthorityRoot { get; }
}

/// <summary>
/// Immutable paths for one parent or child run. The resolved paths define storage,
/// but they do not bypass content admission or filesystem authorization.
/// </summary>
public sealed record SessionStoragePaths
{
    private SessionStoragePaths(
        SessionStorageBinding? binding,
        string sessionDirectory,
        string attachmentStagingDirectory,
        string artifactDirectory,
        ManagedTemporaryLocation managedTemporary,
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
        ManagedTemporary = managedTemporary;
        WorktreeDirectory = NormalizeAbsolute(worktreeDirectory, nameof(worktreeDirectory));
        LogPath = NormalizeAbsolute(logPath, nameof(logPath));
        CurrentSessionRoots = currentSessionRoots
            .Select(root => NormalizeAbsolute(root, nameof(currentSessionRoots)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        LegacyLogsBasePath = legacyLogsBasePath;
    }

    /// <summary>Gets the durable versioned binding. A null value identifies an unchanged legacy layout.</summary>
    public SessionStorageBinding? Binding { get; }
    /// <summary>Gets the session workspace and default relative-path base.</summary>
    public string SessionDirectory { get; }
    /// <summary>Gets the directory for untrusted attachments before content admission.</summary>
    public string AttachmentStagingDirectory { get; }
    /// <summary>Gets the current run's retained artifact directory.</summary>
    public string ArtifactDirectory { get; }
    /// <summary>Gets the current run's managed temporary location.</summary>
    public ManagedTemporaryLocation ManagedTemporary { get; }
    /// <summary>Gets the session-owned directory for Git worktrees.</summary>
    public string WorktreeDirectory { get; }
    /// <summary>Gets the current run's raw session log path.</summary>
    public string LogPath { get; }
    /// <summary>Gets the ordinary filesystem authority roots for the current session.</summary>
    public IReadOnlyList<string> CurrentSessionRoots { get; }
    private string? LegacyLogsBasePath { get; }

    /// <summary>Creates the version-2 parent layout below one persisted envelope.</summary>
    /// <param name="envelopeRoot">The persisted envelope root.</param>
    /// <returns>The resolved parent paths.</returns>
    public static SessionStoragePaths CreateVersion2(SessionStorageEnvelopeRoot envelopeRoot)
    {
        var root = envelopeRoot.Value;
        return new SessionStoragePaths(
            new SessionStorageBinding(SessionStorageLayoutVersion.Version2, envelopeRoot),
            Path.Combine(root, "workspace"),
            Path.Combine(root, "attachment-staging"),
            Path.Combine(root, "artifacts"),
            new ManagedTemporaryLocation(Path.Combine(root, "tmp", "parent"), root),
            Path.Combine(root, "worktrees"),
            Path.Combine(root, "logs", "session.log"),
            [root],
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
            new ManagedTemporaryLocation(
                Path.Combine(normalizedSessionDirectory, "tmp", "parent"),
                normalizedSessionDirectory),
            Path.Combine(normalizedSessionDirectory, "worktrees"),
            Path.Combine(normalizedLogsBase, sanitizedSessionId, "session.log"),
            [normalizedSessionDirectory, normalizedLogsBase],
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
                Path.Combine(childRoot, "artifacts"),
                new ManagedTemporaryLocation(Path.Combine(childRoot, "tmp"), binding.EnvelopeRoot.Value),
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
            new ManagedTemporaryLocation(Path.Combine(childRootLegacy, "tmp"), SessionDirectory),
            WorktreeDirectory,
            Path.Combine(
                LegacyLogsBasePath
                ?? throw new InvalidOperationException("Legacy storage is missing its log base."),
                sanitizedScopeId,
                "session.log"),
            CurrentSessionRoots,
            LegacyLogsBasePath);
    }

    internal static string NormalizeAbsolute(string path, string parameterName)
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
