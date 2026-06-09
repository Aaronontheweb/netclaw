// -----------------------------------------------------------------------
// <copyright file="MattermostProactiveThreadTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Mattermost.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostProactiveOutboundClientTests
{
    private static readonly MattermostChannelOptions DefaultOptions = new()
    {
        AllowDirectMessages = true,
        AllowedUserIds = ["u-1", "u-2"],
        AllowedChannelIds = ["ch-1", "ch-2"]
    };

    [Fact]
    public async Task Rejects_disallowed_user()
    {
        var client = CreateClient();

        var result = await SendAsync(client, "hello", userId: "u-bad");

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
        var client = CreateClient(options: options);

        var result = await SendAsync(client, "hello", userId: "u-1");

        Assert.Contains("Direct messages are disabled", result);
    }

    [Fact]
    public async Task Rejects_disallowed_channel()
    {
        var client = CreateClient();

        var result = await SendAsync(client, "hello", channelId: "ch-bad");

        Assert.Contains("not in the allowed channels list", result);
    }

    [Fact]
    public async Task Successful_dm_uses_allowed_user_id()
    {
        var fake = new FakeMattermostOutboundClient();
        var client = CreateClient(outbound: fake);

        var result = await SendAsync(client, "hello user", userId: "u-1");

        Assert.Contains("Message sent to user u-1", result);
        Assert.Single(fake.OpenedDms);
        Assert.Equal("u-1", fake.OpenedDms[0].Value);
    }

    [Fact]
    public async Task Successful_channel_message_posts_and_wires_session()
    {
        var fake = new FakeMattermostOutboundClient();
        var client = CreateClient(outbound: fake);

        var result = await SendAsync(client, "hello channel", channelId: "ch-1");

        Assert.Equal("mattermost", client.Key.Value);
        Assert.Contains("Message sent to channel ch-1", result);
        Assert.Single(fake.PostedThreads);
        Assert.Equal("ch-1", fake.PostedThreads[0].ChannelId.Value);
        Assert.Equal("hello channel", fake.PostedThreads[0].Text);
    }

    private static Task<string> SendAsync(
        MattermostProactiveOutboundClient client,
        string text,
        string? channelId = null,
        string? userId = null)
    {
        var request = userId is not null
            ? new ChannelSendRequest(ChannelAddressKind.DirectMessage, userId, text)
            : new ChannelSendRequest(ChannelAddressKind.Destination, channelId!, text);
        return client.SendMessageAsync(request, CancellationToken.None);
    }

    private static MattermostProactiveOutboundClient CreateClient(
        FakeMattermostOutboundClient? outbound = null,
        MattermostChannelOptions? options = null,
        Func<MattermostChannelId?>? defaultChannelIdAccessor = null,
        Func<IActorRef?>? gatewayAccessor = null)
    {
        return new MattermostProactiveOutboundClient(
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

public sealed class MattermostAddressResolverTests
{
    private const string AllowedUserId = "abcdefghijklmnopqrstuvwxyz";
    private const string OtherUserId = "bcdefghijklmnopqrstuvwxyza";
    private const string AllowedChannelId = "12345678901234567890123456";
    private const string OtherChannelId = "23456789012345678901234567";

    [Fact]
    public async Task User_resolver_resolves_exact_user_id_without_directory_lookup()
    {
        var tool = CreateUserResolver(new MattermostChannelOptions
        {
            AllowedUserIds = [AllowedUserId]
        });
        var request = new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost),
            ChannelAddressKind.User,
            AllowedUserId);

        var result = await tool.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Resolved, result.Status);
        Assert.Equal(AllowedUserId, result.RequireSingle().StableId);
    }

    [Fact]
    public async Task User_resolver_filters_exact_user_id_through_allowed_users()
    {
        var tool = CreateUserResolver(new MattermostChannelOptions
        {
            AllowedUserIds = [AllowedUserId]
        });
        var request = new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost),
            ChannelAddressKind.User,
            OtherUserId);

        var result = await tool.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.NotFound, result.Status);
        Assert.Contains("not in the allowed users list", result.Error);
    }

    [Fact]
    public async Task Destination_resolver_resolves_exact_channel_id()
    {
        var resolver = new MattermostDestinationAddressResolver(
            new MattermostChannelOptions { AllowedChannelIds = [AllowedChannelId] },
            () => null);
        var request = new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost),
            ChannelAddressKind.Destination,
            $"channel:{AllowedChannelId}");

        var result = await resolver.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.Resolved, result.Status);
        Assert.Equal(AllowedChannelId, result.RequireSingle().StableId);
    }

    [Fact]
    public async Task Destination_resolver_filters_exact_channel_id_through_allowed_channels()
    {
        var resolver = new MattermostDestinationAddressResolver(
            new MattermostChannelOptions { AllowedChannelIds = [AllowedChannelId] },
            () => null);
        var request = new ChannelAddressResolutionRequest(
            ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost),
            ChannelAddressKind.Destination,
            OtherChannelId);

        var result = await resolver.ResolveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ChannelAddressResolutionStatus.NotFound, result.Status);
        Assert.Contains("not in the allowed channels list", result.Error);
    }

    private static LookupMattermostUserTool CreateUserResolver(MattermostChannelOptions options)
    {
        return new LookupMattermostUserTool(
            () => throw new InvalidOperationException("Directory lookup should not be used for exact IDs."),
            options);
    }
}
