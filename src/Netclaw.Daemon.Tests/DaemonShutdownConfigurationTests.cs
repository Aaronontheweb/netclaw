// -----------------------------------------------------------------------
// <copyright file="DaemonShutdownConfigurationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Daemon;
using Xunit;

namespace Netclaw.Daemon.Tests;

/// <summary>
/// Regression coverage for the canary daemon-stop finding (see
/// <see cref="DaemonConfig.GracefulShutdownBudget"/> remarks): the Akka CoordinatedShutdown
/// "before-service-unbind" phase timeout — where session draining actually happens — must
/// track <see cref="DaemonConfig.GracefulShutdownBudget"/>, not a hardcoded literal that can
/// silently drift out of sync with the CLI's SIGTERM wait or the generated systemd unit's
/// TimeoutStopSec.
/// </summary>
public sealed class DaemonShutdownConfigurationTests
{
    [Fact]
    public void BuildCoordinatedShutdownHocon_UsesGracefulShutdownBudget()
    {
        var hocon = DaemonShutdownConfiguration.BuildCoordinatedShutdownHocon(DaemonConfig.GracefulShutdownBudget);

        Assert.Contains(
            $"phases.before-service-unbind.timeout = {(int)DaemonConfig.GracefulShutdownBudget.TotalSeconds}s",
            hocon,
            StringComparison.Ordinal);
        Assert.Contains("exit-clr = off", hocon, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(200)]
    [InlineData(600)]
    public void BuildCoordinatedShutdownHocon_InterpolatesArbitraryTimeouts(int seconds)
    {
        var hocon = DaemonShutdownConfiguration.BuildCoordinatedShutdownHocon(TimeSpan.FromSeconds(seconds));

        Assert.Contains($"phases.before-service-unbind.timeout = {seconds}s", hocon, StringComparison.Ordinal);
    }
}
