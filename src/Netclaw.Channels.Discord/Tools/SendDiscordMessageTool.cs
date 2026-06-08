// -----------------------------------------------------------------------
// <copyright file="SendDiscordMessageTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Channels.Discord.Tools;

/// <summary>
/// LLM tool that posts a proactive message to a Discord channel or DM. Channel
/// posts create a Discord thread; DMs use the root DM message as the session
/// anchor. The session is wired into the actor hierarchy so user replies route
/// back to a live session.
/// </summary>
[NetclawTool("send_discord_message",
    "Send a message to a Discord channel or DM a user, creating a new conversation session. " +
    "Use this to proactively notify users or start discussions. " +
    "Provide channel_id for a channel post, user_id for a DM, or omit both to use the configured default channel.",
    Grant = "builtin")]
public sealed partial class SendDiscordMessageTool : NetclawTool<SendDiscordMessageTool.Params>
{
    private const int MaxThreadNameLength = 100;

    private readonly IDiscordOutboundClient _outboundClient;
    private readonly DiscordChannelOptions _options;
    private readonly Func<IActorRef?> _gatewayAccessor;

    public record Params(
        [property: Description("The message text to send")]
        string Message,
        [property: Description("Discord channel ID to post to. Mutually exclusive with user_id. Defaults to the configured default channel if both are omitted.")]
        string? ChannelId = null,
        [property: Description("Discord user ID to DM. Mutually exclusive with channel_id.")]
        string? UserId = null,
        [property: Description("Optional name for the conversation thread created on the message.")]
        string? ThreadName = null);

    public SendDiscordMessageTool(
        IDiscordOutboundClient outboundClient,
        DiscordChannelOptions options,
        Func<IActorRef?> gatewayAccessor)
    {
        _outboundClient = outboundClient;
        _options = options;
        _gatewayAccessor = gatewayAccessor;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Message))
            return "Error: 'message' parameter is required.";

        var gateway = _gatewayAccessor();
        if (gateway is null)
            return "Error: Discord gateway is not connected.";

        var hasChannel = !string.IsNullOrWhiteSpace(args.ChannelId);
        var hasUser = !string.IsNullOrWhiteSpace(args.UserId);

        if (hasChannel && hasUser)
            return "Error: Provide only one of 'channel_id' or 'user_id'.";

        if (hasUser)
            return await SendDirectMessageAsync(args, gateway, ct);

        var defaultChannelId = string.IsNullOrWhiteSpace(_options.DefaultChannelId)
            ? (DiscordChannelId?)null
            : new DiscordChannelId(_options.DefaultChannelId);

        var channelIdValue = !string.IsNullOrWhiteSpace(args.ChannelId)
            ? args.ChannelId!
            : defaultChannelId?.Value;

        if (string.IsNullOrWhiteSpace(channelIdValue))
            return "Error: No 'channel_id' provided and no default Discord channel is configured.";

        var targetChannelId = new DiscordChannelId(channelIdValue);

        if (!DiscordAclPolicy.IsAllowedChannel(targetChannelId, _options, defaultChannelId))
            return $"Error: Channel {targetChannelId.Value} is not in the allowed channels list.";

        var threadName = "Conversation";
        if (!string.IsNullOrWhiteSpace(args.ThreadName))
        {
            threadName = args.ThreadName!.Length > MaxThreadNameLength
                ? args.ThreadName![..MaxThreadNameLength]
                : args.ThreadName!;
        }

        DiscordNewThread result;
        try
        {
            result = await _outboundClient.PostNewThreadAsync(targetChannelId, args.Message, threadName, ct);
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

    private async Task<string> SendDirectMessageAsync(Params args, IActorRef gateway, CancellationToken ct)
    {
        if (!_options.AllowDirectMessages)
            return "Error: Direct messages are disabled. Enable AllowDirectMessages in Discord configuration to send DMs.";

        var userId = new DiscordUserId(args.UserId!);
        if (!DiscordAclPolicy.IsAllowedUser(userId, _options))
            return $"Error: User {userId.Value} is not in the allowed users list.";

        DiscordNewDirectMessage result;
        try
        {
            result = await _outboundClient.PostDirectMessageAsync(userId, args.Message, ct);
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
