// -----------------------------------------------------------------------
// <copyright file="SlackProactiveOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Slack implementation of <see cref="IChannelOutboundClient"/>: ACL-checks the
/// destination, posts a proactive message to a channel (or opens a DM channel
/// first), and wires the new thread into the actor hierarchy so user replies
/// route back to a live session. Distinct from <see cref="ISlackOutboundClient"/>,
/// which is the raw Slack API transport this class orchestrates.
/// </summary>
public sealed class SlackProactiveOutboundClient : IChannelOutboundClient
{
    private readonly ISlackOutboundClient _outboundClient;
    private readonly SlackChannelOptions _options;
    private readonly Func<SlackChannelId?> _defaultChannelIdAccessor;
    private readonly Func<IActorRef?> _gatewayAccessor;

    public SlackProactiveOutboundClient(
        ISlackOutboundClient outboundClient,
        SlackChannelOptions options,
        Func<SlackChannelId?> defaultChannelIdAccessor,
        Func<IActorRef?> gatewayAccessor)
    {
        _outboundClient = outboundClient;
        _options = options;
        _defaultChannelIdAccessor = defaultChannelIdAccessor;
        _gatewayAccessor = gatewayAccessor;
    }

    public ChannelDescriptorKey Key => ChannelDescriptorKey.FromChannelType(ChannelType.Slack);

    public async Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var gateway = _gatewayAccessor();
        if (gateway is null)
            return "Error: Slack gateway is not connected.";

        var isDirectMessage = request.AddressKind == ChannelAddressKind.DirectMessage;

        SlackChannelId targetChannelId;
        if (isDirectMessage)
        {
            if (!_options.AllowDirectMessages)
                return "Error: Direct messages are disabled. Enable AllowDirectMessages in Slack configuration to send DMs.";

            var userId = new SlackUserId(request.TargetId);

            if (!SlackAclPolicy.IsAllowedUser(userId, _options))
                return $"Error: User {userId.Value} is not in the allowed users list.";

            try
            {
                targetChannelId = await _outboundClient.OpenDmChannelAsync(userId, ct);
            }
            catch (Exception ex)
            {
                return $"Error: Failed to open DM channel: {ex.Message}";
            }
        }
        else if (request.AddressKind == ChannelAddressKind.Destination)
        {
            targetChannelId = new SlackChannelId(request.TargetId);

            if (!SlackAclPolicy.IsAllowedChannel(targetChannelId, _options, _defaultChannelIdAccessor()))
                return $"Error: Channel {targetChannelId.Value} is not in the allowed channels list.";
        }
        else
        {
            return $"Error: Slack outbound send does not support address kind '{request.AddressKind}'.";
        }

        SlackNewThread result;
        try
        {
            result = await _outboundClient.PostNewThreadAsync(targetChannelId, request.Text, ct);
        }
        catch (Exception ex)
        {
            return $"Error: Failed to post message to Slack: {ex.Message}";
        }

        var sessionId = new SessionId($"{result.ChannelId.Value}/{result.ThreadTs.Value}");
        var target = isDirectMessage ? $"user {request.TargetId}" : $"channel {request.TargetId}";

        try
        {
            await gateway.Ask<ProactiveThreadAck>(
                new StartProactiveThread(result.ChannelId, result.ThreadTs, sessionId),
                TimeSpan.FromSeconds(30),
                ct);
        }
        catch (Exception)
        {
            // Message was already posted to Slack; the pipeline just didn't initialize
            return $"Message sent to {target} but session pipeline failed to initialize. " +
                   $"Thread: {result.ChannelId.Value}/{result.ThreadTs.Value}";
        }

        return $"Message sent to {target}. Thread: {result.ChannelId.Value}/{result.ThreadTs.Value}";
    }
}
