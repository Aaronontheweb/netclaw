// -----------------------------------------------------------------------
// <copyright file="SystemdUnitPathDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class SystemdUnitPathDoctorCheckTests
{
    [Fact]
    public async Task ReturnsPass_WhenPlatformDisabled()
    {
        var unitPath = WriteUnit("[Service]\nExecStart=/opt/netclaw/netclawd\n");
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: false);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Not applicable", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsPass_WhenUnitFileDoesNotExist()
    {
        var unitPath = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"), "netclaw.service");
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("No systemd user service installed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsWarning_WhenPathDirectiveMissing()
    {
        var unitPath = WriteUnit("""
            [Unit]
            Description=Netclaw Daemon

            [Service]
            Type=simple
            ExecStart=/opt/netclaw/netclawd
            Environment=DOTNET_ENVIRONMENT=Production
            """);
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("does not set PATH", result.Message, StringComparison.Ordinal);
        Assert.Contains("daemon uninstall", result.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsWarning_WhenPathMissingInstallDir()
    {
        var unitPath = WriteUnit("""
            [Service]
            ExecStart=/opt/netclaw/netclawd
            Environment=PATH=/usr/local/bin:/usr/bin:/bin
            """);
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("does not include the daemon's install directory", result.Message, StringComparison.Ordinal);
        Assert.Contains("/opt/netclaw", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsPass_WhenPathContainsInstallDir()
    {
        var unitPath = WriteUnit("""
            [Service]
            ExecStart=/home/user/.local/bin/netclawd
            Environment=PATH=/home/user/.local/bin:/usr/local/bin:/usr/bin:/bin
            """);
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("/home/user/.local/bin", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsWarning_WhenExecStartMissing()
    {
        var unitPath = WriteUnit("""
            [Service]
            Type=simple
            Environment=PATH=/usr/local/bin:/usr/bin:/bin
            """);
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("missing ExecStart", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParsesExecStart_StrippingArguments()
    {
        // ExecStart with arguments — install directory is the binary's parent.
        var unitPath = WriteUnit("""
            [Service]
            ExecStart=/opt/netclaw/netclawd --foreground
            Environment=PATH=/opt/netclaw:/usr/bin
            """);
        var check = new SystemdUnitPathDoctorCheck(unitPath, enabledOnThisPlatform: true);

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    private static string WriteUnit(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "netclaw.service");
        File.WriteAllText(path, content);
        return path;
    }
}
