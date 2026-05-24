// -----------------------------------------------------------------------
// <copyright file="InitResetService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Init;

/// <summary>
/// Executes the two destructive options surfaced by
/// <see cref="InitStartOverDialog"/>: <c>Reset setup only</c> wipes only
/// configuration + identity artifacts; <c>Full reset</c> wipes the entire
/// netclaw root directory.
/// </summary>
/// <remarks>
/// All operations are guarded — callers SHALL NOT invoke these without a
/// completed double confirmation per simplify-netclaw-init §4.
/// </remarks>
public static class InitResetService
{
    /// <summary>
    /// Reset setup only: removes config, secrets, identity files, and the
    /// seeded agents directory. Preserves workspaces, sessions, memory,
    /// skills, and any non-config artifacts a user may have customized.
    /// </summary>
    public static InitResetReport ResetSetupOnly(NetclawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var removed = new List<string>();

        TryDeleteFile(paths.NetclawConfigPath, removed);
        TryDeleteFile(paths.SecretsPath, removed);
        TryDeleteFile(paths.SoulPath, removed);
        TryDeleteFile(paths.ToolingPath, removed);
        TryDeleteFile(paths.AgentsPath, removed);
        TryDeleteDirectory(paths.AgentsDirectory, removed);

        return new InitResetReport(InitStartOverAction.ResetSetup, removed);
    }

    /// <summary>
    /// Full reset: removes the entire netclaw root directory tree. Includes
    /// workspaces, sessions, memory, skills, the daemon database, and every
    /// other artifact under <see cref="NetclawPaths.BasePath"/>.
    /// </summary>
    /// <remarks>
    /// Refuses to operate on suspicious roots (filesystem root, user-profile
    /// root, OS-temp root, or anything missing a Netclaw marker file). The
    /// guard is precautionary — an operator who misconfigures
    /// <c>NETCLAW_HOME</c> SHOULD see a refusal, not a wiped home directory.
    /// </remarks>
    public static InitResetReport FullReset(NetclawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        AssertSafeFullResetRoot(paths.BasePath);

        var removed = new List<string>();
        if (Directory.Exists(paths.BasePath))
        {
            // Track the root before deletion; per-file enumeration would be
            // O(n) noise for a full nuke.
            removed.Add(paths.BasePath);
            try
            {
                Directory.Delete(paths.BasePath, recursive: true);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Full reset failed while removing '{paths.BasePath}': {ex.Message}", ex);
            }
        }

        return new InitResetReport(InitStartOverAction.FullReset, removed);
    }

    private static void AssertSafeFullResetRoot(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            throw new InvalidOperationException("Full reset refused: BasePath is empty.");

        var normalized = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(basePath));

        // Refuse filesystem root, user-profile root, temp root.
        var dangerous = new[]
        {
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetPathRoot(normalized) ?? "/")),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
        };

        foreach (var bad in dangerous)
        {
            if (string.Equals(normalized, bad, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Full reset refused: BasePath '{normalized}' matches a protected root ('{bad}'). " +
                    "Set NETCLAW_HOME to a dedicated subdirectory (e.g., $HOME/.netclaw) before retrying.");
            }
        }

        // If the directory exists, require a Netclaw marker (config or
        // identity directory) before nuking. A directory without either is
        // probably not a Netclaw install — refuse and ask the operator to
        // verify.
        if (Directory.Exists(normalized))
        {
            var configMarker = Path.Combine(normalized, "netclaw.json");
            var identityMarker = Path.Combine(normalized, "identity");
            if (!File.Exists(configMarker) && !Directory.Exists(identityMarker))
            {
                throw new InvalidOperationException(
                    $"Full reset refused: '{normalized}' does not contain a Netclaw marker " +
                    "(netclaw.json or identity/). Refusing to recursively delete an unrecognized directory.");
            }
        }
    }

    private static void TryDeleteFile(string path, List<string> removed)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        removed.Add(path);
    }

    private static void TryDeleteDirectory(string path, List<string> removed)
    {
        if (!Directory.Exists(path)) return;
        Directory.Delete(path, recursive: true);
        removed.Add(path);
    }
}

/// <summary>Report of artifacts removed by a reset action.</summary>
public sealed record InitResetReport(InitStartOverAction Action, IReadOnlyList<string> RemovedPaths);
