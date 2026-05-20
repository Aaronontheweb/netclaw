// -----------------------------------------------------------------------
// <copyright file="MattermostActionEndpointExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Akka.Actor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Mattermost;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class MattermostActionEndpointExtensionsTests
{
    [Fact]
    public async Task Valid_callback_returns_success_after_session_ack()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var actionStore = new MattermostCallbackActionStore(time);
        var token = actionStore.CreateAction("ch-1", "call-1", ApprovalOptionKeys.ApproveOnce, "root-1", "requester-1");
        var pipeline = new RecordingPipeline(_ => CommandAck.For(new SessionId("ch-1/root-1")));

        await using var app = await CreateHostAsync(time, actionStore, pipeline);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/mattermost/actions", new
        {
            user_id = "requester-1",
            post_id = "root-1",
            channel_id = "ch-1",
            context = new Dictionary<string, string> { ["action_token"] = token }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("Once", payload.GetProperty("ephemeral_text").GetString());
        Assert.Single(pipeline.Feedback);
        var feedback = Assert.IsType<ToolInteractionResponse>(pipeline.Feedback[0]);
        Assert.Equal("call-1", feedback.CallId.Value);
        Assert.Equal("requester-1", feedback.SenderId.Value);
    }

    [Fact]
    public async Task Replayed_callback_token_is_rejected()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var actionStore = new MattermostCallbackActionStore(time);
        var token = actionStore.CreateAction("ch-1", "call-1", ApprovalOptionKeys.ApproveOnce, "root-1", "requester-1");
        var pipeline = new RecordingPipeline(_ => CommandAck.For(new SessionId("ch-1/root-1")));

        await using var app = await CreateHostAsync(time, actionStore, pipeline);
        var client = app.GetTestClient();
        var body = new
        {
            user_id = "requester-1",
            post_id = "root-1",
            channel_id = "ch-1",
            context = new Dictionary<string, string> { ["action_token"] = token }
        };

        var first = await client.PostAsJsonAsync("/api/mattermost/actions", body, TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync("/api/mattermost/actions", body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondPayload = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("no longer valid", secondPayload.GetProperty("ephemeral_text").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(pipeline.Feedback);
    }

    [Fact]
    public async Task Wrong_requester_returns_explicit_rejection_message()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var actionStore = new MattermostCallbackActionStore(time);
        var token = actionStore.CreateAction("ch-1", "call-1", ApprovalOptionKeys.ApproveOnce, "root-1", "requester-1");
        var pipeline = new RecordingPipeline(_ => CommandNack.For(new SessionId("ch-1/root-1"), "approval_wrong_requester"));

        await using var app = await CreateHostAsync(time, actionStore, pipeline);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/mattermost/actions", new
        {
            user_id = "requester-1",
            post_id = "root-1",
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
        var pipeline = new RecordingPipeline(_ => CommandNack.For(new SessionId("ch-1/root-1"), "approval_prompt_expired"));

        await using var app = await CreateHostAsync(time, actionStore, pipeline);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/mattermost/actions", new
        {
            user_id = "requester-1",
            post_id = "root-1",
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
        var pipeline = new RecordingPipeline(_ => CommandAck.For(new SessionId("ch-1/root-1")));

        await using var app = await CreateHostAsync(time, actionStore, pipeline);
        var client = app.GetTestClient();
        var oversized = new string('x', MattermostActionEndpointExtensions.MaxCallbackBodyBytes + 1);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mattermost/actions")
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(pipeline.Feedback);
    }

    [Fact]
    public async Task Callback_endpoint_rate_limits_after_policy_threshold()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z"));
        var actionStore = new MattermostCallbackActionStore(time);
        var pipeline = new RecordingPipeline(_ => CommandAck.For(new SessionId("ch-1/root-1")));

        await using var app = await CreateHostAsync(time, actionStore, pipeline, useRealRateLimiter: true);
        var client = app.GetTestClient();

        for (var i = 0; i < 30; i++)
        {
            var response = await client.PostAsJsonAsync("/api/mattermost/actions", new
            {
                user_id = "requester-1",
                post_id = "root-1",
                channel_id = "ch-1",
                context = new Dictionary<string, string> { ["action_token"] = $"missing-{i}" }
            }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var limited = await client.PostAsJsonAsync("/api/mattermost/actions", new
        {
            user_id = "requester-1",
            post_id = "root-1",
            channel_id = "ch-1",
            context = new Dictionary<string, string> { ["action_token"] = "missing-over-limit" }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    private static async Task<WebApplication> CreateHostAsync(
        FakeTimeProvider time,
        MattermostCallbackActionStore actionStore,
        RecordingPipeline pipeline,
        bool useRealRateLimiter = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton(new MattermostChannelOptions
        {
            Enabled = true,
            CallbackUrl = "https://netclaw.example.com/api/mattermost/actions",
            AllowedUserIds = ["requester-1"]
        });
        builder.Services.AddSingleton(actionStore);
        builder.Services.AddSingleton<ISessionPipeline>(pipeline);
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

    private sealed class RecordingPipeline(Func<IWithSessionId, object> responder) : ISessionPipeline
    {
        public List<IWithSessionId> Feedback { get; } = [];

        public Task<MaterializedSession> CreateAsync(SessionId sessionId, SessionPipelineOptions options, Akka.Streams.IMaterializer? materializer = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
        {
            Feedback.Add(feedback);
            return Task.CompletedTask;
        }

        public Task<object> SendFeedbackAndWaitAsync(IWithSessionId feedback, TimeSpan timeout, CancellationToken ct = default)
        {
            Feedback.Add(feedback);
            return Task.FromResult(responder(feedback));
        }
    }
}
