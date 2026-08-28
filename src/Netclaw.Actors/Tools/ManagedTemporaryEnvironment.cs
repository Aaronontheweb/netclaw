// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryEnvironment.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;

namespace Netclaw.Actors.Tools;

internal static class ManagedTemporaryEnvironment
{
    internal static string? Prepare(ProcessStartInfo startInfo, string temporaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            if (!Directory.Exists(temporaryDirectory))
                return $"Error: Managed temporary directory '{temporaryDirectory}' was not created.";

            startInfo.Environment["TMPDIR"] = temporaryDirectory;
            startInfo.Environment["TMP"] = temporaryDirectory;
            startInfo.Environment["TEMP"] = temporaryDirectory;
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
