// -----------------------------------------------------------------------
// <copyright file="SessionStorageLocations.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>Normalizes absolute paths shared by session storage value objects.</summary>
internal static class SessionStoragePathValue
{
    public static string NormalizeAbsolute(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The path must be absolute.", parameterName);

        return Path.GetFullPath(path);
    }
}

/// <summary>A canonical absolute session workspace directory.</summary>
public readonly record struct SessionWorkspaceDirectory
{
    /// <summary>Creates a session workspace directory.</summary>
    /// <param name="value">The absolute directory path.</param>
    public SessionWorkspaceDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute directory for untrusted attachment staging.</summary>
public readonly record struct AttachmentStagingDirectory
{
    /// <summary>Creates an attachment staging directory.</summary>
    /// <param name="value">The absolute directory path.</param>
    public AttachmentStagingDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute directory for retained run artifacts.</summary>
public readonly record struct ArtifactDirectory
{
    /// <summary>Creates an artifact directory.</summary>
    /// <param name="value">The absolute directory path.</param>
    public ArtifactDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute directory for disposable run files.</summary>
public readonly record struct ManagedTemporaryDirectory
{
    /// <summary>Creates a managed temporary directory.</summary>
    /// <param name="value">The absolute directory path.</param>
    public ManagedTemporaryDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute root which contains one managed temporary directory.</summary>
public readonly record struct ManagedTemporaryStorageRoot
{
    /// <summary>Creates a managed temporary storage root.</summary>
    /// <param name="value">The absolute directory path.</param>
    public ManagedTemporaryStorageRoot(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute directory for session Git worktrees.</summary>
public readonly record struct WorktreeDirectory
{
    /// <summary>Creates a worktree directory.</summary>
    /// <param name="value">The absolute directory path.</param>
    public WorktreeDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>A canonical absolute path for one raw session log.</summary>
public readonly record struct SessionLogPath
{
    /// <summary>Creates a raw session log path.</summary>
    /// <param name="value">The absolute file path.</param>
    public SessionLogPath(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    /// <summary>Gets the canonical absolute file path.</summary>
    public string Value { get; }

    /// <summary>Returns the canonical absolute path.</summary>
    public override string ToString() => Value;
}

/// <summary>Identifies the configured log directory used by sessions without a storage binding.</summary>
internal readonly record struct LegacySessionLogsDirectory
{
    /// <summary>Creates a legacy session-log directory.</summary>
    /// <param name="value">The absolute directory path.</param>
    public LegacySessionLogsDirectory(string value)
    {
        Value = SessionStoragePathValue.NormalizeAbsolute(value, nameof(value));
    }

    /// <summary>Gets the canonical absolute directory path.</summary>
    public string Value { get; }
}
