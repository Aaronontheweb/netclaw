// -----------------------------------------------------------------------
// <copyright file="DiscordChannelHealthTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class DiscordChannelHealthTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override Config? Config =>
        ConfigurationFactory.ParseString("akka.test.default-timeout = 5s");

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Reports_healthy_only_when_gateway_is_ready()
    {
        var gateway = new FakeDiscordGatewayClient
        {
            IsConnected = true,
            IsReady = true,
            BotUserId = new DiscordUserId("bot-1")
        };
        var channel = CreateChannel(gateway);

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelHealthStatus.Healthy, health.Status);
        Assert.Null(health.Detail);
    }

    [Fact]
    public async Task Reports_degraded_when_gateway_is_connected_but_not_ready()
    {
        var gateway = new FakeDiscordGatewayClient
        {
            IsConnected = true,
            IsReady = false,
            HealthDetail = "Discord.Net resumed a stale gateway session."
        };
        var channel = CreateChannel(gateway);

        var health = await channel.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChannelHealthStatus.Degraded, health.Status);
        Assert.Equal("Discord.Net resumed a stale gateway session.", health.Detail);
    }

    private DiscordChannel CreateChannel(
        FakeDiscordGatewayClient gatewayClient,
        TimeProvider? timeProvider = null)
    {
        var replyClient = new UnconfiguredDiscordReplyClient();

        return new DiscordChannel(
            Sys,
            pipeline: null!,
            new SessionIngressGate(),
            gatewayClient,
            replyClient,
            TestChannelRegistries.DiscordWithProcessingRenderer(replyClient),
            new NullContentScanner(),
            SafePromptInjectionDetector.Instance,
            new FakeHttpClientFactory(),
            null,
            NullNotificationSink.Instance,
            timeProvider ?? TimeProvider.System,
            new DiscordChannelOptions
            {
                Enabled = true,
                BotToken = new SensitiveString("test-token"),
                AllowedChannelIds = ["ch-1"]
            },
            NullLogger<DiscordChannel>.Instance,
            new ToolConfig
            {
                AudienceProfiles = TestDiscordGatewayDeps.DefaultAudienceProfiles
            },
            TestDiscordGatewayDeps.DefaultVisionCapableModel,
            TestDiscordGatewayDeps.NewTestPaths());
    }

    private sealed class FakeDiscordGatewayClient : IDiscordGatewayClient
    {
        private Func<string, Task>? _cleanReconnectRequired;

        public event Func<DiscordGatewayMessage, Task>? MessageReceived
        {
            add { }
            remove { }
        }

        public event Func<DiscordGatewayInteraction, Task>? InteractionReceived
        {
            add { }
            remove { }
        }

        public event Func<string, Task>? CleanReconnectRequired
        {
            add => _cleanReconnectRequired += value;
            remove => _cleanReconnectRequired -= value;
        }

        public event Func<DiscordGatewaySnapshot, Task>? ConnectionRestored
        {
            add { }
            remove { }
        }

        public bool IsConnected { get; set; }
        public bool IsReady { get; set; }
        public string? HealthDetail { get; set; }
        public DiscordUserId? BotUserId { get; set; }
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public Queue<bool> ConnectReadyResults { get; } = new();
        public bool RaiseCleanReconnectDuringFirstConnect { get; init; }

        public Task<DiscordGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot());

        public async Task<DiscordGatewaySnapshot> ConnectAsync(
            string botToken,
            CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            IsConnected = true;
            IsReady = NextConnectReady();
            HealthDetail = IsReady ? null : "Discord gateway connected but not ready.";
            BotUserId ??= new DiscordUserId("bot-1");

            if (RaiseCleanReconnectDuringFirstConnect && ConnectCount == 1 && _cleanReconnectRequired is not null)
                await _cleanReconnectRequired("Discord.Net resumed a previous gateway session.");

            return Snapshot();
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            IsConnected = false;
            IsReady = false;
            HealthDetail = "Discord gateway disconnected.";
            return Task.CompletedTask;
        }

        public Task RaiseCleanReconnectRequiredAsync(string reason) =>
            _cleanReconnectRequired?.Invoke(reason) ?? Task.CompletedTask;

        private bool NextConnectReady()
        {
            if (ConnectReadyResults.TryDequeue(out var ready))
                return ready;

            return true;
        }

        private DiscordGatewaySnapshot Snapshot() =>
            new(IsConnected, IsReady, HealthDetail, BotUserId);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
