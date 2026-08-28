// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryEnvironmentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Tools;
using Netclaw.Tests.Utilities;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ManagedTemporaryEnvironmentTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Child_process_receives_all_temporary_variables_without_daemon_mutation()
    {
        var managed = Path.Combine(_directory.Path, "managed");
        var daemonTmpDir = Environment.GetEnvironmentVariable("TMPDIR");
        var daemonTmp = Environment.GetEnvironmentVariable("TMP");
        var daemonTemp = Environment.GetEnvironmentVariable("TEMP");
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh",
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("Write-Output \"$env:TMPDIR|$env:TMP|$env:TEMP\"");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("printf '%s' \"$TMPDIR|$TMP|$TEMP\"");
        }

        Assert.Null(ManagedTemporaryEnvironment.Prepare(startInfo, managed));
        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal($"{managed}|{managed}|{managed}", output.Trim());
        Assert.Equal(daemonTmpDir, Environment.GetEnvironmentVariable("TMPDIR"));
        Assert.Equal(daemonTmp, Environment.GetEnvironmentVariable("TMP"));
        Assert.Equal(daemonTemp, Environment.GetEnvironmentVariable("TEMP"));
    }

    [Fact]
    public async Task Dotnet_temporary_api_returns_the_managed_directory()
    {
        var managed = Path.Combine(_directory.Path, "dotnet-managed");
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("[Console]::Write([IO.Path]::GetTempPath())");

        Assert.Null(ManagedTemporaryEnvironment.Prepare(startInfo, managed));
        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(managed)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(output.Trim())));
    }

    [Fact]
    public void Preparation_failure_does_not_inject_a_host_fallback()
    {
        var blockingFile = Path.Combine(_directory.Path, "blocking-file");
        File.WriteAllText(blockingFile, "not a directory");
        var managed = Path.Combine(blockingFile, "managed");
        var startInfo = new ProcessStartInfo();
        startInfo.Environment.Remove("TMPDIR");
        startInfo.Environment.Remove("TMP");
        startInfo.Environment.Remove("TEMP");

        var error = ManagedTemporaryEnvironment.Prepare(startInfo, managed);

        Assert.StartsWith("Error preparing managed temporary directory:", error, StringComparison.Ordinal);
        Assert.False(startInfo.Environment.ContainsKey("TMPDIR"));
        Assert.False(startInfo.Environment.ContainsKey("TMP"));
        Assert.False(startInfo.Environment.ContainsKey("TEMP"));
    }
}
