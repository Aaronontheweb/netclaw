// -----------------------------------------------------------------------
// <copyright file="DaemonPathEnvironmentFileTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Daemon;

/// <summary>
/// Unit tests for the daemon PATH provisioning contract shared by install
/// (producer), doctor --fix (rehydrator), and the systemd PATH doctor check
/// (validator). <c>InstallAsync</c>/<c>UninstallAsync</c> themselves are not
/// driven here: they invoke real <c>systemctl</c>/<c>loginctl</c> against the
/// live <c>netclaw.service</c> and the real <c>~/.config</c> unit path, so
/// exercising them in-process would mutate the developer's own service. The
/// pure builders below are exactly the content those methods write and read.
/// </summary>
public sealed class DaemonPathEnvironmentFileTests
{
    [Fact]
    public void ComposePathValue_PrependsInstallDir()
        => Assert.Equal(
            "/opt/netclaw:/home/u/.dotnet:/usr/bin",
            DaemonPathEnvironmentFile.ComposePathValue("/opt/netclaw", "/home/u/.dotnet:/usr/bin"));

    [Fact]
    public void ComposePathValue_EmptyCapture_YieldsInstallDirOnly()
    {
        // A missing PATH on the installing shell is its own broken state — we don't
        // paper over it with an invented default directory list.
        Assert.Equal("/opt/netclaw", DaemonPathEnvironmentFile.ComposePathValue("/opt/netclaw", null));
        Assert.Equal("/opt/netclaw", DaemonPathEnvironmentFile.ComposePathValue("/opt/netclaw", ""));
    }

    [Fact]
    public void Render_ThenReadPathValue_RoundTrips()
    {
        var content = DaemonPathEnvironmentFile.Render("/opt/netclaw", "/home/u/.dotnet:/usr/bin");

        Assert.Equal("PATH=/opt/netclaw:/home/u/.dotnet:/usr/bin\n", content);
        Assert.Equal("/opt/netclaw:/home/u/.dotnet:/usr/bin", DaemonPathEnvironmentFile.ReadPathValue(content));
    }

    [Fact]
    public void ReadPathValue_NoPathAssignment_ReturnsNull()
        => Assert.Null(DaemonPathEnvironmentFile.ReadPathValue("FOO=bar\nBAZ=qux\n"));

    [Theory]
    [InlineData("/opt/netclaw:/usr/bin", "/opt/netclaw", true)]
    [InlineData("/usr/local/bin:/usr/bin", "/opt/netclaw", false)]
    public void PathContainsDirectory_MatchesExactEntry(string pathValue, string dir, bool expected)
        => Assert.Equal(expected, DaemonPathEnvironmentFile.PathContainsDirectory(pathValue, dir));

    [Fact]
    public void TryGetInstallDir_FromExecStart_StripsBinaryAndArgs()
    {
        var lines = new[] { "[Service]", "ExecStart=/opt/netclaw/netclawd --foreground" };

        Assert.True(DaemonPathEnvironmentFile.TryGetInstallDir(lines, out var dir));
        Assert.Equal("/opt/netclaw", dir);
    }

    [Fact]
    public void TryGetInstallDir_NoExecStart_ReturnsFalse()
        => Assert.False(DaemonPathEnvironmentFile.TryGetInstallDir(new[] { "[Service]" }, out _));

    [Fact]
    public void TryGetEnvironmentFilePath_StripsTolerantDashPrefix()
    {
        var lines = new[] { "EnvironmentFile=-/home/u/.netclaw/config/daemon.env" };

        Assert.True(DaemonPathEnvironmentFile.TryGetEnvironmentFilePath(lines, out var path));
        Assert.Equal("/home/u/.netclaw/config/daemon.env", path);
    }

    [Fact]
    public void TryGetEnvironmentFilePath_Absent_ReturnsFalse()
        => Assert.False(DaemonPathEnvironmentFile.TryGetEnvironmentFilePath(
            new[] { "ExecStart=/opt/netclaw/netclawd" }, out _));

    [Fact]
    public void BuildDaemonUnitContent_WiresEnvironmentFile_AndOmitsInlinePath()
    {
        var unit = DaemonManager.BuildDaemonUnitContent(
            "/opt/netclaw/netclawd",
            "/opt/netclaw/netclaw",
            "/home/u/.netclaw/config/daemon.env");

        Assert.Contains("EnvironmentFile=-/home/u/.netclaw/config/daemon.env", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment=PATH=", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/opt/netclaw/netclawd", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStop=/opt/netclaw/netclaw daemon stop", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveDaemonEnvironmentFile_DeletesFile_AndIsIdempotent()
    {
        // Covers the uninstall env-file-removal contract without driving the
        // systemctl-coupled UninstallAsync.
        var basePath = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.DaemonEnvironmentFilePath, "PATH=/opt/netclaw:/usr/bin\n");
        var manager = new DaemonManager(paths, TimeProvider.System);

        manager.RemoveDaemonEnvironmentFile();
        Assert.False(File.Exists(paths.DaemonEnvironmentFilePath));

        // Idempotent — a second call on an already-removed file must not throw.
        manager.RemoveDaemonEnvironmentFile();
    }
}
