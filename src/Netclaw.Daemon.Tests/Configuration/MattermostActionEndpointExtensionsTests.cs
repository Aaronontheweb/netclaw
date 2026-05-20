// -----------------------------------------------------------------------
// <copyright file="MattermostActionEndpointExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Mattermost;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class MattermostActionEndpointExtensionsTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Valid_callback_routes_through_gateway_and_returns_success_after_ack()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var actionStore = new MattermostCallbackActionStore(time);
        var token = actionStore.CreateAction("ch-1", "call-1", ApprovalOptionKeys.ApproveOnce, "root-1", "requester-1");
        var recorder = new GatewayInteractionRecorder();

        await using var app = await CreateHostAsync(
            time,
            actionStore,
            gatewayResponseFactory: _ => CommandAck.For(new SessionId("ch-1/root-1")),
            recorder: recorder);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/mattermost/actions", new
        {
            user_id = "requester-1",
            post_id = "prompt-55",
            channel_id = "ch-1",
            context = new Dictionary<string, string> { ["action_token"] = token }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("Once", payload.GetProperty("ephemeral_text").GetString());

        var interaction = await recorder.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ch-1", interaction.ChannelId.Value);
        Assert.Equal("root-1", interaction.RootPostId.Value);
        Assert.Equal("call-1", interaction.CallId);
        Assert.Equal(ApprovalOptionKeys.ApproveOnce, interaction.SelectedKey);
        Assert.Equal("requester-1", interaction.SenderId.Value);
        Assert.Equal("requester-1", interaction.RequesterSenderId!.Value.Value);
    }

    [Fact]
    public async Task Replayed_callback_token_is_rejected()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var actionStore = new MattermostCallbackActionStore(time);
        var token = actionStore.CreateAction("ch-1", "call-1", ApprovalOptionKeys.ApproveOnce, "root-1", "requester-1");
        var recorder = new GatewayInteractionRecorder();

        await using var app = await CreateHostAsync(
            time,
            actionStore,
            gatewayResponseFactory: _ => CommandAck.For(new SessionId("ch-1/root-1")),
            recorder: recorder);
        var client = app.GetTestClient();
        var body = new
        {
            user_id = "requester-1",
            post_id = "prompt-55",
            channel_id = "ch-1",
            context = new Dictionary<string, string> { ["action_token"] = token }
        };

        var first = await client.PostAsJsonAsync("/api/mattermost/actions", body, TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync("/api/mattermost/actions", body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondPayload = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("no longer valid", secondPayload.GetProperty("ephemeral_text").GetString(), StringComparison.OrdinalIgnoreCase);

        var interaction = await recorder.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("call-1", interaction.CallId);
        Assert.False(recorder.HasPendingInteraction);
    }

    [Fact]
    public async Task Wrong_requester_returns_explicit_rejection_message()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var actionStore = new MattermostCallbackActionStore(time);
        var token = actionStore.CreateAction("ch-1", "call-1", ApprovalOptionKeys.ApproveOnce, "root-1", "requester-1");

        await using var app = await CreateHostAsync(
            time,
            actionStore,
            gatewayResponseFactory: _ => CommandNack.For(new SessionId("ch-1/root-1"), "approval_wrong_requester"),
            recorder: new GatewayInteractionRecorder());
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/mattermost/actions", new
        {
            user_id = "requester-1",
            post_id = "prompt-55",
            channel_id = "ch-1",
            context = new Dictionary<string, string> { ["action_token"] = token }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("Only the requesting user", payload.GetProperty("ephemeral_text").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Expired_prompt_returns_explicit_rejection_message()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var actionStore = new MattermostCallbackActionStore(time);
        var token = actionStore.CreateAction("ch-1", "call-1", ApprovalOptionKeys.ApproveOnce, "root-1", "requester-1");

        await using var app = await CreateHostAsync(
            time,
            actionStore,
            gatewayResponseFactory: _ => CommandNack.For(new SessionId("ch-1/root-1"), "approval_prompt_expired"),
            recorder: new GatewayInteractionRecorder());
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/mattermost/actions", new
        {
            user_id = "requester-1",
            post_id = "prompt-55",
            channel_id = "ch-1",
            context = new Dictionary<string, string> { ["action_token"] = token }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("expired", payload.GetProperty("ephemeral_text").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Oversized_body_returns_413_before_processing()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var actionStore = new MattermostCallbackActionStore(time);
        var recorder = new GatewayInteractionRecorder();

        await using var app = await CreateHostAsync(
            time,
            actionStore,
            gatewayResponseFactory: _ => CommandAck.For(new SessionId("ch-1/root-1")),
            recorder: recorder);
        var client = app.GetTestClient();
        var oversized = new string('x', MattermostActionEndpointExtensions.MaxCallbackBodyBytes + 1);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mattermost/actions")
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(recorder.HasPendingInteraction);
    }

    [Fact]
    public async Task Callback_endpoint_rate_limits_after_policy_threshold()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var actionStore = new MattermostCallbackActionStore(time);

        await using var app = await CreateHostAsync(
            time,
            actionStore,
            gatewayResponseFactory: _ => CommandAck.For(new SessionId("ch-1/root-1")),
            recorder: new GatewayInteractionRecorder(),
            useRealRateLimiter: true);
        var client = app.GetTestClient();

        for (var i = 0; i < 30; i++)
        {
            var response = await client.PostAsJsonAsync("/api/mattermost/actions", new
            {
                user_id = "requester-1",
                post_id = "prompt-55",
                channel_id = "ch-1",
                context = new Dictionary<string, string> { ["action_token"] = $"missing-{i}" }
            }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var limited = await client.PostAsJsonAsync("/api/mattermost/actions", new
        {
            user_id = "requester-1",
            post_id = "prompt-55",
            channel_id = "ch-1",
            context = new Dictionary<string, string> { ["action_token"] = "missing-over-limit" }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Callback_accepts_clicked_prompt_post_id_instead_of_thread_root()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var actionStore = new MattermostCallbackActionStore(time);
        var token = actionStore.CreateAction("ch-1", "call-1", ApprovalOptionKeys.ApproveOnce, "root-1", "requester-1");
        var recorder = new GatewayInteractionRecorder();

        await using var app = await CreateHostAsync(
            time,
            actionStore,
            gatewayResponseFactory: _ => CommandAck.For(new SessionId("ch-1/root-1")),
            recorder: recorder);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/mattermost/actions", new
        {
            user_id = "requester-1",
            post_id = "prompt-post-99",
            channel_id = "ch-1",
            context = new Dictionary<string, string> { ["action_token"] = token }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var interaction = await recorder.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("root-1", interaction.RootPostId.Value);
    }

    private async Task<WebApplication> CreateHostAsync(
        FakeTimeProvider time,
        MattermostCallbackActionStore actionStore,
        Func<MattermostGatewayInteraction, object> gatewayResponseFactory,
        GatewayInteractionRecorder recorder,
        bool useRealRateLimiter = false)
    {
        var gateway = Sys.ActorOf(Props.Create(() => new GatewayResponderActor(gatewayResponseFactory, recorder)));
        ActorRegistry.For(Sys).Register<MattermostGatewayActorKey>(gateway);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton<ActorSystem>(Sys);
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton(new MattermostChannelOptions
        {
            Enabled = true,
            CallbackUrl = "https://netclaw.example.com/api/mattermost/actions",
            AllowedUserIds = ["requester-1"]
        });
        builder.Services.AddSingleton(actionStore);
        builder.Services.AddLogging();

        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy(MattermostActionEndpointExtensions.CallbackRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = useRealRateLimiter ? 30 : 10_000,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    }));
            options.RejectionStatusCode = 429;
        });

        var app = builder.Build();
        app.UseRateLimiter();
        app.MapMattermostActionEndpoint();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class GatewayResponderActor : ReceiveActor
    {
        public GatewayResponderActor(
            Func<MattermostGatewayInteraction, object> gatewayResponseFactory,
            GatewayInteractionRecorder recorder)
        {
            Receive<MattermostGatewayInteraction>(interaction =>
            {
                recorder.Record(interaction);
                Sender.Tell(gatewayResponseFactory(interaction));
            });
        }
    }

    private sealed class GatewayInteractionRecorder
    {
        private readonly Channel<MattermostGatewayInteraction> _channel = Channel.CreateUnbounded<MattermostGatewayInteraction>();

        public bool HasPendingInteraction => _channel.Reader.TryPeek(out _);

        public void Record(MattermostGatewayInteraction interaction)
            => _channel.Writer.TryWrite(interaction);

        public async Task<MattermostGatewayInteraction> ReadAsync(CancellationToken cancellationToken)
            => await _channel.Reader.ReadAsync(cancellationToken);
    }
}
