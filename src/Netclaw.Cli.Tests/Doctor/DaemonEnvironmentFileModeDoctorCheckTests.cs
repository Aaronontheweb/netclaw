// -----------------------------------------------------------------------
// <copyright file="DaemonEnvironmentFileModeDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class DaemonEnvironmentFileModeDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Passes_when_there_is_no_daemon_env()
    {
        var result = await new DaemonEnvironmentFileModeDoctorCheck(NewPaths()).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite)]
    [InlineData(UnixFileMode.UserRead)]
    public async Task Passes_when_only_the_owner_has_access(UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        var paths = NewPaths();
        File.WriteAllText(paths.DaemonEnvironmentFilePath, "NETCLAW_HOME=/x/home\n");
        File.SetUnixFileMode(paths.DaemonEnvironmentFilePath, mode);

        var result = await new DaemonEnvironmentFileModeDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    public async Task Warns_with_the_path_and_the_fix_when_group_or_others_have_access(UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        var paths = NewPaths();
        File.WriteAllText(paths.DaemonEnvironmentFilePath, "NETCLAW_HOME=/x/home\n");
        File.SetUnixFileMode(paths.DaemonEnvironmentFilePath, mode);

        var result = await new DaemonEnvironmentFileModeDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains(paths.DaemonEnvironmentFilePath, result.Message, StringComparison.Ordinal);
        Assert.Contains("netclaw doctor --fix", result.Remediation, StringComparison.Ordinal);
    }

    private NetclawPaths NewPaths()
    {
        var paths = new NetclawPaths(Path.Combine(_temp.Path, Guid.NewGuid().ToString("N")));
        paths.EnsureDirectoriesExist();
        return paths;
    }
}
