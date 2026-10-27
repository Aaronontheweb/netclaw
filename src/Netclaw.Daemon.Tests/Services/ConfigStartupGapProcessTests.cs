// -----------------------------------------------------------------------
// <copyright file="ConfigStartupGapProcessTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigStartupGapProcessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Latest_config_reaches_the_real_host_across_watcher_start(bool writeBeforeWatcherStart)
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(Path.Combine(directory.Path, "home"));
        var port = StandInDaemon.GetFreePort();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;

        WriteConfig(paths, port, "127.0.0.1");
        await using var daemon = new OwnedDaemon(directory.Path, paths, port);
        await daemon.WaitForAsync(state => state.Starts == 1 && state.Watchers == 1, token);
        Assert.Equal(1, await ReadGenerationAsync("127.0.0.1", port, token));

        await using (var gate = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            await gate.OpenAsync(token);
            await using (var mode = gate.CreateCommand())
            {
                mode.CommandText = "PRAGMA journal_mode=WAL";
                Assert.Equal("wal", await mode.ExecuteScalarAsync(token));
            }

            await using (var acquire = gate.CreateCommand())
            {
                acquire.CommandText = "BEGIN EXCLUSIVE";
                await acquire.ExecuteNonQueryAsync(token);
            }

            // WAL permits readers. Verify that this transaction excludes another writer before A starts.
            await AssertWalWriteGateAsync(gate.ConnectionString, token);
            WriteConfig(paths, port, "127.0.0.2");
            await daemon.WaitForAsync(state => state.Migrations == 2, token);
            daemon.DrainEvents();
            Assert.Equal(1, daemon.State.Watchers);
            Assert.Equal(1, daemon.State.Starts);

            // Program loaded A before this checkpoint. The memory schema UPDATE holds the new watcher behind the gate.
            if (writeBeforeWatcherStart)
                WriteConfig(paths, port, "127.0.0.3");

            await using var release = gate.CreateCommand();
            release.CommandText = "ROLLBACK";
            await release.ExecuteNonQueryAsync(token);
        }

        if (!writeBeforeWatcherStart)
        {
            await daemon.WaitForAsync(state => state.Starts == 2 && state.Watchers == 2, token);
            Assert.Equal(2, await ReadGenerationAsync("127.0.0.2", port, token));
            WriteConfig(paths, port, "127.0.0.3");
        }

        await daemon.WaitForAsync(state => state.Host == "127.0.0.3" && state.Starts == state.Migrations, token);
        var generation = await ReadGenerationAsync("127.0.0.3", port, token);
        Assert.True(generation > 1, "B must serve the readiness response from a later host generation.");
        Assert.Equal(daemon.State.Migrations, generation);
        Assert.Equal(0, daemon.State.Rejections);
    }

    private static async Task AssertWalWriteGateAsync(string connectionString, CancellationToken token)
    {
        await using var probe = new SqliteConnection(connectionString);
        await probe.OpenAsync(token);
        await using var read = probe.CreateCommand();
        read.CommandText = "SELECT count(*) FROM schema_version";
        Assert.True(Convert.ToInt64(await read.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) > 0);

        await using var write = probe.CreateCommand();
        write.CommandTimeout = 1;
        // A zero-row UPDATE still needs the write lock, as does the startup memory repair.
        write.CommandText = "UPDATE memory_documents SET recall_mode = recall_mode WHERE 0";
        var error = await Assert.ThrowsAsync<SqliteException>(() => write.ExecuteNonQueryAsync(token));
        Assert.Equal(5, error.SqliteErrorCode);
    }

    private static void WriteConfig(NetclawPaths paths, int port, string host)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.NetclawConfigPath)!);
        var temporary = paths.NetclawConfigPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            configVersion = 1,
            Daemon = new { Host = host, Port = port },
            Memory = new { Enabled = false, Embeddings = new { Enabled = false } },
            SkillFeeds = new { Feeds = Array.Empty<object>() },
            ExternalSkills = new { Sources = Array.Empty<object>() },
            Logging = new { Console = new { Enabled = true } }
        }));
        File.Move(temporary, paths.NetclawConfigPath, overwrite: true);
    }

    private static async Task<int> ReadGenerationAsync(string host, int port, CancellationToken token)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var response = await client.GetAsync($"http://{host}:{port}/api/health/ready", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return int.Parse(Assert.Single(response.Headers.GetValues("X-Netclaw-Generation")), CultureInfo.InvariantCulture);
    }

    private sealed class OwnedDaemon : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Channel<string> _events = Channel.CreateUnbounded<string>();
        private readonly Task _stdout;
        private readonly Task _stderr;

        public LifecycleState State { get; private set; } = new(0, 0, 0, 0, "none");

        public OwnedDaemon(string directory, NetclawPaths paths, int port)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "netclawd.dll"));
            foreach (var key in start.Environment.Keys.Where(key =>
                         key.StartsWith("NETCLAW_", StringComparison.OrdinalIgnoreCase)
                         || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                         || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            start.Environment["NETCLAW_HOME"] = paths.BasePath;
            start.Environment["NETCLAW_Daemon__Port"] = port.ToString(CultureInfo.InvariantCulture);
            start.Environment["HOME"] = directory;
            start.Environment["USERPROFILE"] = directory;
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(directory, ".config");
            start.Environment["DOTNET_NOLOGO"] = "1";
            foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
                start.Environment[key] = "http://127.0.0.1:1";
            start.Environment["NO_PROXY"] = "127.0.0.1,127.0.0.2,127.0.0.3,localhost";
            start.Environment["no_proxy"] = start.Environment["NO_PROXY"];
            StandInDaemon.RequireDaemonBindPort(start.Environment);
            _process = Process.Start(start) ?? throw new InvalidOperationException("The test daemon did not start.");
            _stdout = ReadEventsAsync(_process.StandardOutput);
            _stderr = ReadEventsAsync(_process.StandardError);
        }

        public async Task WaitForAsync(Func<LifecycleState, bool> predicate, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                while (!predicate(State))
                    Apply(await _events.Reader.ReadAsync(timeout.Token));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Assert.Fail($"The daemon did not reach the lifecycle checkpoint. Counts and host: {State}; exited: {_process.HasExited}.");
            }
        }

        public void DrainEvents()
        {
            while (_events.Reader.TryRead(out var signal))
                Apply(signal);
        }

        private void Apply(string signal)
        {
            State = signal switch
            {
                "migration" => State with { Migrations = State.Migrations + 1 },
                "watcher" => State with { Watchers = State.Watchers + 1 },
                "started" => State with { Starts = State.Starts + 1 },
                "rejected" => State with { Rejections = State.Rejections + 1 },
                _ => State with { Host = signal }
            };
        }

        private async Task ReadEventsAsync(StreamReader stream)
        {
            while (await stream.ReadLineAsync() is { } line)
            {
                // Keep only synthetic addresses and lifecycle signals. Do not retain daemon output.
                string? signal = null;
                if (line.Contains("Running SQLite schema migrations at", StringComparison.Ordinal))
                    signal = "migration";
                else if (line.Contains("Config hot-reload watching:", StringComparison.Ordinal))
                    signal = "watcher";
                else if (line.Contains("Application started.", StringComparison.Ordinal))
                    signal = "started";
                else if (line.Contains("Config validation failed.", StringComparison.Ordinal))
                    signal = "rejected";
                else if (line.Contains("Now listening on:", StringComparison.Ordinal))
                    signal = new[] { "127.0.0.1", "127.0.0.2", "127.0.0.3" }
                        .SingleOrDefault(host => line.Contains($"http://{host}:", StringComparison.Ordinal));

                if (signal is not null)
                    await _events.Writer.WriteAsync(signal);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(_stdout, _stderr).WaitAsync(timeout.Token);
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    private sealed record LifecycleState(int Migrations, int Watchers, int Starts, int Rejections, string Host);
}
