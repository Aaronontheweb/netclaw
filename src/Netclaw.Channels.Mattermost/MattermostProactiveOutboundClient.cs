// -----------------------------------------------------------------------
// <copyright file="MattermostProactiveOutboundClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels.Mattermost;

/// <summary>
/// Mattermost implementation of <see cref="IChannelOutboundClient"/>: ACL-checks
/// the destination, posts a proactive message to a channel (or opens a DM
/// channel first), and wires the new thread into the actor hierarchy so user
/// replies route back to a live session. Distinct from
/// <see cref="IMattermostOutboundClient"/>, which is the raw Mattermost API
/// transport this class orchestrates.
/// </summary>
public sealed class MattermostProactiveOutboundClient : IChannelOutboundClient
{
    private readonly IMattermostOutboundClient _outboundClient;
    private readonly MattermostChannelOptions _options;
    private readonly Func<MattermostChannelId?> _defaultChannelIdAccessor;
    private readonly Func<IActorRef?> _gatewayAccessor;

    public MattermostProactiveOutboundClient(
        IMattermostOutboundClient outboundClient,
        MattermostChannelOptions options,
        Func<MattermostChannelId?> defaultChannelIdAccessor,
        Func<IActorRef?> gatewayAccessor)
    {
        _outboundClient = outboundClient;
        _options = options;
        _defaultChannelIdAccessor = defaultChannelIdAccessor;
        _gatewayAccessor = gatewayAccessor;
    }

    public ChannelDescriptorKey Key => ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);

    public async Task<string> SendMessageAsync(ChannelSendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var gateway = _gatewayAccessor();
        if (gateway is null)
            return "Error: Mattermost gateway is not connected.";

        var isDirectMessage = request.AddressKind == ChannelAddressKind.DirectMessage;

        MattermostChannelId targetChannelId;
        if (isDirectMessage)
        {
            if (!_options.AllowDirectMessages)
                return "Error: Direct messages are disabled. Enable AllowDirectMessages in Mattermost configuration to send DMs.";

            var userId = new MattermostUserId(request.TargetId);

            if (!MattermostAclPolicy.IsAllowedUser(userId, _options))
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
            targetChannelId = new MattermostChannelId(request.TargetId);

            if (!MattermostAclPolicy.IsAllowedChannel(targetChannelId, _options, _defaultChannelIdAccessor()))
                return $"Error: Channel {targetChannelId.Value} is not in the allowed channels list.";
        }
        else
        {
            return $"Error: Mattermost outbound send does not support address kind '{request.AddressKind}'.";
        }

        MattermostNewThread result;
        try
        {
            result = await _outboundClient.PostNewThreadAsync(targetChannelId, request.Text, ct);
        }
        catch (Exception ex)
        {
            return $"Error: Failed to post message to Mattermost: {ex.Message}";
        }

        var sessionId = new SessionId($"{result.ChannelId.Value}/{result.RootPostId.Value}");
        var target = isDirectMessage ? $"user {request.TargetId}" : $"channel {request.TargetId}";

        try
        {
            await gateway.Ask<MattermostProactiveThreadAck>(
                new StartMattermostProactiveThread(result.ChannelId, result.RootPostId, sessionId),
                TimeSpan.FromSeconds(30),
                ct);
        }
        catch (Exception)
        {
            return $"Message sent to {target} but session pipeline failed to initialize. " +
                   $"Thread: {result.ChannelId.Value}/{result.RootPostId.Value}";
        }

        return $"Message sent to {target}. Thread: {result.ChannelId.Value}/{result.RootPostId.Value}";
    }
}
