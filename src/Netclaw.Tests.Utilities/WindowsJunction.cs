// -----------------------------------------------------------------------
// <copyright file="WindowsJunction.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;

namespace Netclaw.Tests.Utilities;

/// <summary>
/// Creates a Windows directory junction with <c>mklink /J</c>. A junction needs
/// no administrator rights or developer mode, so Windows CI can run link-escape
/// regressions that a symbolic link cannot run there.
/// </summary>
public static class WindowsJunction
{
    public static async Task CreateAsync(string link, string target, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("cmd.exe did not start.");
        await process.WaitForExitAsync(ct);
        var standardOutput = await process.StandardOutput.ReadToEndAsync(ct);
        var standardError = await process.StandardError.ReadToEndAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mklink failed: {standardOutput}{standardError}");

        if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
            throw new InvalidOperationException("mklink did not create a reparse point.");
    }
}
