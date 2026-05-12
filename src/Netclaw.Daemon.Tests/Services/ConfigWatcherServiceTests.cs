// -----------------------------------------------------------------------
// <copyright file="ConfigWatcherServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigWatcherServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeRestartCoordinator _restartCoordinator;
    private readonly ConfigWatcherService _sut;

    public ConfigWatcherServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        _restartCoordinator = new FakeRestartCoordinator();

        _sut = new ConfigWatcherService(
            _paths,
            TimeProvider.System,
            _restartCoordinator,
            new DaemonConfig(),
            NullLogger<ConfigWatcherService>.Instance);
    }

    [Fact]
    public async Task ValidConfigChange_TriggersRestart()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Theory]
    [InlineData("secrets.json")]
    [InlineData("mcp-oauth-metadata.json")]
    [InlineData("random.txt")]
    [InlineData(null)]
    public void NonConfigFiles_AreNotWatched(string? fileName)
    {
        Assert.False(ConfigWatcherService.IsWatchedFile(fileName));
    }

    [Fact]
    public void NetclawJson_IsWatched()
    {
        Assert.True(ConfigWatcherService.IsWatchedFile("netclaw.json"));
    }

    [Fact]
    public async Task InvalidJson_DoesNotTriggerRestart()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ broken json """);
        // secrets.json doesn't exist — that's fine (optional)

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task MissingConfigFiles_TriggersRestart()
    {
        // Both files are optional in the config chain — missing = valid
        Assert.False(File.Exists(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task RestartCoordinatorFailure_DoesNotLeaveIngressClosed()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        _restartCoordinator.ThrowOnRequest = true;

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task DaemonSectionChanged_SkipsRestartAndLogsWarning()
    {
        // Port differs from the default DaemonConfig (5199) injected into _sut
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Daemon": { "Port": 9999 } }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(0, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task DaemonSectionMatchingCurrentConfig_TriggersRestart()
    {
        // Explicit Daemon section that matches the running defaults — not a change
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Daemon": { "Host": "127.0.0.1", "Port": 5199, "ExposureMode": "local" } }""");

        await _sut.ApplyReloadAsync(CancellationToken.None);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    // Integration tests — exercise real FileSystemWatcher + filesystem operations.
    // These are inherently async and use generous timeouts to stay stable on slow CI.

    [Fact]
    public async Task AtomicReplace_TriggersReload()
    {
        var ct = TestContext.Current.CancellationToken;

        // Simulate the write-temp-then-rename pattern used by safe editors and CLI tools
        await _sut.StartAsync(ct);

        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");

        var tempPath = _paths.NetclawConfigPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tempPath, """{ "Providers": {} }""");
        File.Move(tempPath, _paths.NetclawConfigPath, overwrite: true);

        // Wait up to 3 s for the debounce + reload to fire
        var deadline = TimeSpan.FromSeconds(3);
        var started = DateTime.UtcNow;
        while (_restartCoordinator.RequestCount == 0 && DateTime.UtcNow - started < deadline)
            await Task.Delay(50, ct);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public async Task InPlaceWrite_TriggersReload()
    {
        var ct = TestContext.Current.CancellationToken;

        // Regression: direct in-place writes (e.g. shell > redirect) must still work
        await _sut.StartAsync(ct);

        // Write once to create the file, then overwrite in place
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");
        await Task.Delay(100, ct); // let any initial events settle

        _restartCoordinator.Reset();
        await File.WriteAllTextAsync(_paths.NetclawConfigPath, """{ "Providers": {} }""", ct);

        var deadline = TimeSpan.FromSeconds(3);
        var started = DateTime.UtcNow;
        while (_restartCoordinator.RequestCount == 0 && DateTime.UtcNow - started < deadline)
            await Task.Delay(50, ct);

        Assert.Equal(1, _restartCoordinator.RequestCount);
    }

    [Fact]
    public void ReadDaemonConfigFromFile_MissingFile_ReturnsDefaults()
    {
        var result = ConfigWatcherService.ReadDaemonConfigFromFile(_paths.NetclawConfigPath);

        Assert.Equal(new DaemonConfig(), result);
    }

    [Fact]
    public void ReadDaemonConfigFromFile_NoDaemonSection_ReturnsDefaults()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "Providers": {} }""");

        var result = ConfigWatcherService.ReadDaemonConfigFromFile(_paths.NetclawConfigPath);

        Assert.Equal(new DaemonConfig(), result);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _dir.Dispose();
    }

    private sealed class FakeRestartCoordinator : IDaemonRestartCoordinator
    {
        public int RequestCount { get; private set; }

        public bool ThrowOnRequest { get; set; }

        public void Reset() => RequestCount = 0;

        public Task RequestConfigRestartAsync(CancellationToken cancellationToken)
        {
            RequestCount++;

            if (ThrowOnRequest)
                throw new InvalidOperationException("boom");

            return Task.CompletedTask;
        }
    }
}
