// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryEnvironment.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

/// <summary>Prepares a child process to use one session-owned temporary directory.</summary>
internal static class ManagedTemporaryEnvironment
{
    /// <summary>
    /// Validates and creates the directory, checks filesystem links before and after creation,
    /// and sets the standard temporary environment variables on the child process only.
    /// </summary>
    /// <returns><c>null</c> on success; otherwise, a stable error that prevents process launch.</returns>
    internal static string? Prepare(
        ProcessStartInfo startInfo,
        string temporaryDirectory,
        string storageRoot)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);

        try
        {
            var normalizedRoot = PathUtility.Normalize(storageRoot);
            var normalizedTemporaryDirectory = PathUtility.Normalize(temporaryDirectory);
            if (!PathUtility.IsWithinRoot(normalizedTemporaryDirectory, normalizedRoot))
                return "Error: The managed temporary directory is outside its storage root.";

            if (PathUtility.ContainsSymlinkSegment(
                    normalizedRoot,
                    normalizedTemporaryDirectory,
                    includeRoot: true))
                return "Error: The managed temporary directory contains an unsafe filesystem link.";

            Directory.CreateDirectory(normalizedTemporaryDirectory);
            if (!Directory.Exists(normalizedTemporaryDirectory))
                return $"Error: Managed temporary directory '{normalizedTemporaryDirectory}' was not created.";

            if (PathUtility.ContainsSymlinkSegment(
                    normalizedRoot,
                    normalizedTemporaryDirectory,
                    includeRoot: true))
                return "Error: The managed temporary directory contains an unsafe filesystem link.";

            startInfo.Environment["TMPDIR"] = normalizedTemporaryDirectory;
            startInfo.Environment["TMP"] = normalizedTemporaryDirectory;
            startInfo.Environment["TEMP"] = normalizedTemporaryDirectory;
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return $"Error preparing managed temporary directory: {ex.Message}";
        }
    }
}
