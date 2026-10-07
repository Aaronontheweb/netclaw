// -----------------------------------------------------------------------
// <copyright file="IdentityTimezoneValidationPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// The identity timezone field must refuse a zone that the reminder scheduler cannot
/// resolve. An existing install may already hold such a value: the form still opens
/// with it shown, and only the save is blocked.
/// </summary>
public sealed class IdentityTimezoneValidationPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public IdentityTimezoneValidationPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(
            _paths.NetclawConfigPath,
            """{ "configVersion": 1, "Identity": { "UserTimezone": "Not/AZone" } }""");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Unknown_stored_timezone_is_shown_and_blocks_the_save()
    {
        var (terminal, app, vm) = HeadlessTerminaFixture.Create<IdentityRedoPage, IdentityRedoViewModel>(
            "/identity-redo",
            () => new IdentityRedoPage(),
            () => new IdentityRedoViewModel(_paths),
            out var input);

        input.EnqueueKey(ConsoleKey.Enter); // agent name
        input.EnqueueKey(ConsoleKey.Enter); // communication style
        input.EnqueueKey(ConsoleKey.Enter); // user name
        input.EnqueueKey(ConsoleKey.Enter); // timezone: "Not/AZone" is rejected
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.False(vm.IsSaved.Value, $"An unknown timezone must not be saved. Screen:\n{terminal}");
        Assert.Equal("Not/AZone", vm.Step.UserTimezone);
        Assert.True(terminal.Contains("Unknown time zone 'Not/AZone' on this system."),
            $"Expected the unknown-zone message. Screen:\n{terminal}");
        Assert.False(File.Exists(_paths.SoulPath), "SOUL.md must not be written for an invalid timezone.");
    }
}
