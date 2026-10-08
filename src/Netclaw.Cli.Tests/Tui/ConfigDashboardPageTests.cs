// -----------------------------------------------------------------------
// <copyright file="ConfigDashboardPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina.Input;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ConfigDashboardPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ConfigDashboardPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ModelsRow_ShowsMainModel_ForNamedDefinitionsAndRolesShape()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Providers": { "anthropic": { "Type": "anthropic" } },
              "Models": {
                "Definitions": { "opus": { "Provider": "anthropic", "ModelId": "claude-opus-4" } },
                "Roles": { "Main": "opus" }
              }
            }
            """);

        await AssertDashboardShowsAsync("claude-opus-4");
    }

    [Fact]
    public async Task ModelsRow_ShowsMainModel_ForLegacyInlineShape()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Providers": { "anthropic": { "Type": "anthropic" } },
              "Models": { "Main": { "Provider": "anthropic", "ModelId": "claude-legacy-3" } }
            }
            """);

        await AssertDashboardShowsAsync("claude-legacy-3");
    }

    private async Task AssertDashboardShowsAsync(string expectedModelId)
    {
        var (terminal, app, _) = HeadlessTerminaFixture.Create<ConfigDashboardPage, ConfigDashboardViewModel>(
            "/config",
            () => new ConfigDashboardPage(),
            () => new ConfigDashboardViewModel(new ConfigDashboardNavigationState(), _paths),
            out var input);

        using var appCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(appCts.Token);

        try
        {
            while (!terminal.Contains("Models"))
            {
                appCts.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            Assert.True(terminal.Contains(expectedModelId), "Models row should show the main model id.");
            Assert.False(terminal.Contains("not set"), "No row should report 'not set' for this config.");
        }
        finally
        {
            input.EnqueueKey(ConsoleKey.Q, control: true);
            await run.WaitAsync(appCts.Token);
        }
    }
}
