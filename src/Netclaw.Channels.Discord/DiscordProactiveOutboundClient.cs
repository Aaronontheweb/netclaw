// -----------------------------------------------------------------------
// <copyright file="DiscordProactiveOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Discord;

/// <summary>
/// Discord implementation of <see cref="IChannelOutboundClient"/>: ACL-checks
/// the destination and posts a proactive message. Channel posts create a
/// Discord thread; DMs use the root DM message as the session anchor. The
/// session is wired into the actor hierarchy so user replies route back to a
/// live session. Distinct from <see cref="IDiscordOutboundClient"/>, which is
/// the raw Discord API transport this class orchestrates.
/// </summary>
public sealed class DiscordProactiveOutboundClient : IChannelOutboundClient
{
    // The generic send_channel_message tool has no thread-name parameter, so
    // proactive channel posts always create the thread with this default name.
    private const string DefaultThreadName = "Conversation";

    private readonly IDiscordOutboundClient _outboundClient;
    private readonly DiscordChannelOptions _options;
    private readonly Func<IActorRef?> _gatewayAccessor;

    public DiscordProactiveOutboundClient(
        IDiscordOutboundClient outboundClient,
        DiscordChannelOptions options,
        Func<IActorRef?> gatewayAccessor)
    {
        _outboundClient = outboundClient;
        _options = options;
        _gatewayAccessor = gatewayAccessor;
    }

    public ChannelDescriptorKey Key => ChannelDescriptorKey.FromChannelType(ChannelType.Discord);

    public async Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var gateway = _gatewayAccessor();
        if (gateway is null)
            return "Error: Discord gateway is not connected.";

        if (request.AddressKind == ChannelAddressKind.DirectMessage)
            return await SendDirectMessageAsync(request, gateway, ct);

        if (request.AddressKind != ChannelAddressKind.Destination)
            return $"Error: Discord outbound send does not support address kind '{request.AddressKind}'.";

        // The default channel is implicitly allowed even when it is absent from
        // AllowedChannelIds, so the ACL check needs it for comparison.
        var defaultChannelId = string.IsNullOrWhiteSpace(_options.DefaultChannelId)
            ? (DiscordChannelId?)null
            : new DiscordChannelId(_options.DefaultChannelId);

        var targetChannelId = new DiscordChannelId(request.TargetId);

        if (!DiscordAclPolicy.IsAllowedChannel(targetChannelId, _options, defaultChannelId))
            return $"Error: Channel {targetChannelId.Value} is not in the allowed channels list.";

        DiscordNewThread result;
        try
        {
            result = await _outboundClient.PostNewThreadAsync(targetChannelId, request.Text, DefaultThreadName, ct);
        }
        catch (DiscordThreadCreationFailedException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return $"Message sent to channel {ex.ChannelId.Value}, but Discord could not create a follow-up thread. "
                   + $"Root message: {ex.RootMessageId.Value}. Reason: {detail}";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to post message to Discord: {ex.Message}";
        }

        var sessionId = new SessionId($"{result.ChannelId.Value}/{result.ThreadOrMessageId.Value}");

        try
        {
            await gateway.Ask<ProactiveThreadAck>(
                new StartProactiveThread(
                    result.ChannelId,
                    result.ReplyChannelId,
                    result.ThreadOrMessageId,
                    sessionId),
                TimeSpan.FromSeconds(30),
                ct);
        }
        catch (Exception)
        {
            // The message was already posted to Discord; only the session
            // pipeline failed to initialize.
            return $"Message sent to channel {targetChannelId.Value} but session pipeline failed to initialize. " +
                   $"Thread: {sessionId.Value}";
        }

        return $"Message sent to channel {targetChannelId.Value}. Thread: {sessionId.Value}";
    }

    private async Task<string> SendDirectMessageAsync(ChannelSendRequest request, IActorRef gateway, CancellationToken ct)
    {
        if (!_options.AllowDirectMessages)
            return "Error: Direct messages are disabled. Enable AllowDirectMessages in Discord configuration to send DMs.";

        var userId = new DiscordUserId(request.TargetId);
        if (!DiscordAclPolicy.IsAllowedUser(userId, _options))
            return $"Error: User {userId.Value} is not in the allowed users list.";

        DiscordNewDirectMessage result;
        try
        {
            result = await _outboundClient.PostDirectMessageAsync(userId, request.Text, ct);
        }
        catch (Exception ex)
        {
            return $"Error: Failed to post direct message to Discord: {ex.Message}";
        }

        var sessionId = new SessionId($"{result.ChannelId.Value}/{result.ThreadOrMessageId.Value}");

        try
        {
            await gateway.Ask<ProactiveThreadAck>(
                new StartProactiveThread(
                    result.ChannelId,
                    result.ReplyChannelId,
                    result.ThreadOrMessageId,
                    sessionId,
                    DirectMessageUserId: result.UserId,
                    RootMessageId: result.RootMessageId),
                TimeSpan.FromSeconds(30),
                ct);
        }
        catch (Exception)
        {
            return $"Message sent to user {userId.Value} but session pipeline failed to initialize. " +
                   $"Thread: {sessionId.Value}";
        }

        return $"Message sent to user {userId.Value}. Thread: {sessionId.Value}";
    }
}
