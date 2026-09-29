// -----------------------------------------------------------------------
// <copyright file="DaemonToolConfigRegistrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Pins the daemon wiring for <see cref="ToolConfig"/>: the daemon configuration sources,
/// the daemon DI registration, and the Program.cs calls to both. A narrowed list in
/// netclaw.json must reach the <see cref="ToolConfig"/> that the tool policy consumes.
/// </summary>
public sealed class DaemonToolConfigRegistrationTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public DaemonToolConfigRegistrationTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Narrowed_team_allowlist_reaches_the_registered_tool_config()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Tools": {
                "AudienceProfiles": {
                  "Team": { "ToolsMode": "Allowlist", "AllowedTools": ["file_read", "file_list"] },
                  "GlobalReadRoots": ["{skills_dir}"]
                }
              }
            }
            """);

        using var provider = BuildDaemonServices(new CapturingLoggerProvider());

        var toolConfig = provider.GetRequiredService<ToolConfig>();
        var team = ToolAudienceProfileDefaults.GetResolvedProfile(toolConfig.AudienceProfiles, TrustAudience.Team);
        Assert.Equal(["file_read", "file_list"], team.AllowedTools);
        Assert.Equal([ToolAudienceProfileDefaults.SkillsDirectoryToken], toolConfig.AudienceProfiles.GlobalReadRoots);
    }

    [Fact]
    public async Task Null_list_is_logged_as_a_startup_warning()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Tools": { "AudienceProfiles": { "Team": { "AllowedTools": null } } }
            }
            """);
        var logs = new CapturingLoggerProvider();

        await using var provider = BuildDaemonServices(logs);
        foreach (var service in provider.GetServices<IHostedService>())
            await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(provider.GetRequiredService<ToolConfig>().AudienceProfiles.Team.AllowedTools);
        var warning = Assert.Single(logs.Messages, message => message.Level == LogLevel.Warning);
        Assert.Equal("Netclaw.Startup", warning.Category);
        Assert.Contains(
            "Tools.AudienceProfiles.Team.AllowedTools is null or an empty object; treating it as an empty list.",
            warning.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Program_uses_the_daemon_configuration_sources_and_tool_config_registration()
    {
        // Program.cs is top-level statements, so no test can build the full daemon host.
        // This check fails if Program.cs goes back to the plain binder, which adds configured
        // list items to the defaults.
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Netclaw.Daemon", "Program.cs"));

        Assert.Contains("configuration.AddNetclawDaemonSources(bootstrapPaths);", program, StringComparison.Ordinal);
        Assert.Contains("services.AddDaemonToolConfig(configuration);", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Get<ToolConfig>", program, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSection(\"Tools\")", program, StringComparison.Ordinal);
    }

    private ServiceProvider BuildDaemonServices(CapturingLoggerProvider logs)
    {
        var configuration = new ConfigurationBuilder()
            .AddNetclawDaemonSources(_paths)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddDaemonToolConfig(configuration);
        return services.BuildServiceProvider();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IMPLEMENTATION_PLAN.md")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }

    private sealed record LogMessage(string Category, LogLevel Level, string Text);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogMessage> _messages = [];

        public IReadOnlyList<LogMessage> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private void Add(LogMessage message)
        {
            lock (_messages)
                _messages.Add(message);
        }

        private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Add(new LogMessage(category, logLevel, formatter(state, exception)));
        }
    }
}
