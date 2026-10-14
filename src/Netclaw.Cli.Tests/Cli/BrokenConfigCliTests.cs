// -----------------------------------------------------------------------
// <copyright file="BrokenConfigCliTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Runs the built CLI as a child process against a netclaw.json or secrets.json that cannot be
/// loaded. Before the fix every command aborted with an unhandled exception (exit 134) and a
/// crash log; now commands that need the config print one line naming the file and exit 1,
/// while doctor, status and help still run.
/// </summary>
public sealed class BrokenConfigCliTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly string _home;
    private string ConfigPath => Path.Combine(_home, "config", "netclaw.json");
    private string SecretsPath => Path.Combine(_home, "config", "secrets.json");

    public BrokenConfigCliTests()
    {
        _home = Path.Combine(_dir.Path, "netclaw-home");
        Directory.CreateDirectory(Path.Combine(_home, "config"));
    }

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("duplicate key", "{\"configVersion\":1,\"configVersion\":1}", "netclaw.json")]
    [InlineData("empty", "", "netclaw.json")]
    [InlineData("truncated", "{\"configVersion\":1,\"Daemon\":{", "netclaw.json")]
    [InlineData("truncated secrets", "{\"Providers\":{", "secrets.json")]
    public async Task Invalid_file_is_reported_by_each_command_instead_of_crashing(string _, string content, string brokenFile)
    {
        WriteValidConfig();
        File.WriteAllText(brokenFile == "secrets.json" ? SecretsPath : ConfigPath, content);

        await AssertReportedAsync(brokenFile);
    }

    [Fact]
    public async Task Dangling_symlink_is_reported_by_each_command_instead_of_crashing()
    {
        // Creating a symbolic link needs a privilege on Windows.
        if (OperatingSystem.IsWindows())
            return;

        File.CreateSymbolicLink(ConfigPath, Path.Combine(_dir.Path, "no-such-target.json"));

        await AssertReportedAsync("netclaw.json");
    }

    [Fact]
    public async Task Unreadable_file_is_reported_by_each_command_instead_of_crashing()
    {
        // Mode 000 means nothing on Windows, and root reads the file anyway.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;

        WriteValidConfig();

        File.SetUnixFileMode(ConfigPath, UnixFileMode.None);
        try
        {
            await AssertReportedAsync("netclaw.json");
        }
        finally
        {
            // The temp directory cannot be cleaned up around an unreadable file.
            File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Comments_and_trailing_commas_load_as_they_do_for_the_daemon()
    {
        File.WriteAllText(ConfigPath, "{\n// a comment\n\"configVersion\":1,\n\"McpServers\":{},\n}\n");

        var mcp = await RunAsync("mcp", "list");
        var doctor = await RunAsync("doctor");

        Assert.Equal(0, mcp.ExitCode);
        Assert.DoesNotContain("Couldn't read", mcp.Output);
        Assert.DoesNotContain("Config Load", doctor.Output);
        Assert.Contains("[PASS] Config Schema", doctor.Output);
        AssertNoCrashLog();
    }

    [Theory]
    [InlineData("config")]
    [InlineData("mcp")]
    [InlineData("model")]
    [InlineData("secrets")]
    [InlineData("status")]
    [InlineData("doctor")]
    public async Task Help_works_when_the_config_cannot_be_loaded(string command)
    {
        File.WriteAllText(ConfigPath, "{\"configVersion\":1,");

        var help = await RunAsync(command, "--help");

        Assert.True(help.ExitCode == 0, help.Output);
        Assert.Contains("Usage:", help.Output);
        Assert.DoesNotContain("Couldn't read", help.Output);
        AssertNoCrashLog();
    }

    [Fact]
    public async Task Version_works_when_the_config_cannot_be_loaded()
    {
        File.WriteAllText(ConfigPath, "");

        var version = await RunAsync("--version");

        Assert.Equal(0, version.ExitCode);
        Assert.StartsWith("netclaw ", version.Output);
        AssertNoCrashLog();
    }

    private async Task AssertReportedAsync(string brokenFile)
    {
        var expectedPrefix = $"Couldn't read {brokenFile}:";

        foreach (var args in new[] { new[] { "mcp", "list" }, ["model", "list"], ["config", "set", "Daemon.Port", "5200"] })
        {
            var blocked = await RunAsync(args);
            Assert.Equal(1, blocked.ExitCode);
            var line = Assert.Single(blocked.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.StartsWith(expectedPrefix, line);
        }

        var doctor = await RunAsync("doctor");
        Assert.Equal(1, doctor.ExitCode);
        Assert.Contains($"[FAIL] Config Load: {expectedPrefix}", doctor.Output);

        var status = await RunAsync("status");
        Assert.Equal(1, status.ExitCode);
        Assert.Contains(expectedPrefix, status.Output);

        AssertNoCrashLog();
    }

    private void WriteValidConfig()
        => File.WriteAllText(ConfigPath, "{\"configVersion\":1}");

    private void AssertNoCrashLog()
    {
        var logs = Path.Combine(_home, "logs");
        var crashLogs = Directory.Exists(logs) ? Directory.GetFiles(logs, "crash-*") : [];
        Assert.Empty(crashLogs);
    }

    private async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _dir.Path,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "netclaw.dll"));
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("NETCLAW_", StringComparison.Ordinal)).ToList())
            psi.Environment.Remove(key);
        psi.Environment["HOME"] = _dir.Path;
        psi.Environment["NETCLAW_HOME"] = _home;
        psi.Environment["NETCLAW_Daemon__DisableSelfUpdate"] = "true";

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, (await stdout + await stderr).Replace("\r\n", "\n"));
    }
}
