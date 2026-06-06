// -----------------------------------------------------------------------
// <copyright file="MattermostProactiveThreadTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Mattermost.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SendMattermostMessageToolTests
{
    private static readonly MattermostChannelOptions DefaultOptions = new()
    {
        AllowDirectMessages = true,
        AllowedUserIds = ["u-1", "u-2"],
        AllowedChannelIds = ["ch-1", "ch-2"]
    };

    [Fact]
    public async Task Rejects_when_both_channel_and_user_provided()
    {
        var tool = CreateTool();

        var result = await ExecuteAsync(tool, "hello", channelId: "ch-1", userId: "u-1");

        Assert.Contains("exactly one", result);
    }

    [Fact]
    public async Task Rejects_when_neither_provided()
    {
        var tool = CreateTool();

        var result = await ExecuteAsync(tool, "hello");

        Assert.Contains("exactly one", result);
    }

    [Fact]
    public async Task Rejects_disallowed_user()
    {
        var tool = CreateTool();

        var result = await ExecuteAsync(tool, "hello", userId: "u-bad");

        Assert.Contains("not in the allowed users list", result);
    }

    [Fact]
    public async Task Rejects_dm_when_direct_messages_disabled()
    {
        var options = new MattermostChannelOptions
        {
            AllowDirectMessages = false,
            AllowedUserIds = ["u-1"]
        };
        var tool = CreateTool(options: options);

        var result = await ExecuteAsync(tool, "hello", userId: "u-1");

        Assert.Contains("Direct messages are disabled", result);
    }

    [Fact]
    public async Task Rejects_disallowed_channel()
    {
        var tool = CreateTool();

        var result = await ExecuteAsync(tool, "hello", channelId: "ch-bad");

        Assert.Contains("not in the allowed channels list", result);
    }

    [Fact]
    public async Task Successful_dm_uses_allowed_user_id()
    {
        var fake = new FakeMattermostOutboundClient();
        var tool = CreateTool(outbound: fake);

        var result = await ExecuteAsync(tool, "hello user", userId: "u-1");

        Assert.Contains("Message sent to user u-1", result);
        Assert.Single(fake.OpenedDms);
        Assert.Equal("u-1", fake.OpenedDms[0].Value);
    }

    [Fact]
    public async Task Successful_channel_message_posts_and_wires_session()
    {
        var fake = new FakeMattermostOutboundClient();
        var tool = CreateTool(outbound: fake);

        var result = await ExecuteAsync(tool, "hello channel", channelId: "ch-1");

        Assert.Contains("Message sent to channel ch-1", result);
        Assert.Single(fake.PostedThreads);
        Assert.Equal("ch-1", fake.PostedThreads[0].ChannelId.Value);
        Assert.Equal("hello channel", fake.PostedThreads[0].Text);
    }

    private static Task<string> ExecuteAsync(
        SendMattermostMessageTool tool,
        string message,
        string? channelId = null,
        string? userId = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["Message"] = message
        };
        if (channelId is not null)
            args["ChannelId"] = channelId;
        if (userId is not null)
            args["UserId"] = userId;

        return tool.ExecuteAsync(args, CancellationToken.None);
    }

    private static SendMattermostMessageTool CreateTool(
        FakeMattermostOutboundClient? outbound = null,
        MattermostChannelOptions? options = null,
        Func<MattermostChannelId?>? defaultChannelIdAccessor = null,
        Func<IActorRef?>? gatewayAccessor = null)
    {
        return new SendMattermostMessageTool(
            outbound ?? new FakeMattermostOutboundClient(),
            options ?? DefaultOptions,
            defaultChannelIdAccessor ?? (() => null),
            gatewayAccessor ?? (() => new FakeGatewayActor()));
    }

    private sealed class FakeMattermostOutboundClient : IMattermostOutboundClient
    {
        public List<MattermostUserId> OpenedDms { get; } = [];
        public List<(MattermostChannelId ChannelId, string Text)> PostedThreads { get; } = [];

        public Task<MattermostChannelId> OpenDmChannelAsync(MattermostUserId userId, CancellationToken ct = default)
        {
            OpenedDms.Add(userId);
            return Task.FromResult(new MattermostChannelId($"dm-{userId.Value}"));
        }

        public Task<MattermostNewThread> PostNewThreadAsync(
            MattermostChannelId channelId,
            string text,
            CancellationToken ct = default)
        {
            PostedThreads.Add((channelId, text));
            return Task.FromResult(new MattermostNewThread(channelId, new MattermostRootPostId($"root-{channelId.Value}")));
        }
    }

    private sealed class FakeGatewayActor : MinimalActorRef
    {
        public override ActorPath Path { get; } =
            new RootActorPath(Address.AllSystems) / "fake-mattermost-gateway";

        public override IActorRefProvider Provider =>
            throw new NotSupportedException("Not needed for tool tests");

        protected override void TellInternal(object message, IActorRef sender)
        {
            if (message is StartMattermostProactiveThread spt)
                sender.Tell(new MattermostProactiveThreadAck(spt.SessionId));
        }
    }
}
