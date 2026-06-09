// -----------------------------------------------------------------------
// <copyright file="ChannelIntegrationRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Discord.WebSocket;
using Mattermost;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Discord.Transport;
using Netclaw.Channels.Mattermost;
using Netclaw.Channels.Mattermost.Tools;
using Netclaw.Channels.Mattermost.Transport;
using Netclaw.Channels.Slack;
using Netclaw.Channels.Slack.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using SlackNet.Events;
using SlackNet.Extensions.DependencyInjection;
using SlackNet.Interaction.Experimental;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Composition root for the remote chat channel integrations. Each channel
/// is one fluent <c>AddRemoteChatChannel</c> chain; channel-specific
/// registrations that have no generic builder method go through
/// <c>WithServices</c>.
/// </summary>
public static class ChannelIntegrationRegistrationExtensions
{
    public static void AddChannelIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        AddSlackChannel(services, configuration);
        AddDiscordChannel(services, configuration);
        AddMattermostChannel(services, configuration);
    }

    internal static void AddSlackChannel(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRemoteChatChannel<SlackChannel, SlackChannelOptions>(ChannelType.Slack, configuration)
            // Token validity is NOT checked here: an exception thrown from this
            // registration path aborts host construction and crashes the daemon.
            // A missing/invalid token is handled as a contained channel failure in
            // SlackChannel.StartAsync instead (see issue #1033).
            .WithFilesHttpClient()
            .WithReplyClient<ISlackReplyClient, SlackReplyClient>()
            .WithThreadHistory((sp, options) =>
            {
                var slackApi = sp.GetRequiredService<SlackNet.ISlackApiClient>();
                var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
                var contentScanner = sp.GetRequiredService<IContentScanner>();
                var paths = sp.GetRequiredService<NetclawPaths>();
                var toolConfig = sp.GetRequiredService<ToolConfig>();
                var modelCapabilities = sp.GetRequiredService<ModelCapabilities>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SlackThreadHistoryFetcher>();
                return new SlackThreadHistoryFetcher(
                    slackApi.Conversations,
                    options,
                    httpFactory.CreateClient("slack-files"),
                    contentScanner,
                    paths,
                    toolConfig.AudienceProfiles,
                    modelCapabilities,
                    logger);
            })
            .WithOutboundClient<ISlackOutboundClient, SlackOutboundClient>()
            .WithLookupClient<ISlackTargetLookupClient, SlackApiTargetLookupClient>()
            .WithReminderResolver<SlackReminderTargetResolver>()
            .WithResolver((sp, options) => new SlackTargetResolver(
                sp.GetRequiredService<ISlackTargetLookupClient>(),
                options,
                () => string.IsNullOrWhiteSpace(options.DefaultChannelId)
                    ? null
                    : new SlackChannelId(options.DefaultChannelId)))
            .WithServices((channelServices, options) =>
            {
                channelServices.AddSingleton<SlackApprovalHandler>();
                channelServices.AddSingleton<ISlackTargetResolver>(sp =>
                    sp.GetRequiredService<SlackTargetResolver>());
                channelServices.AddSlackNet(c =>
                {
                    // Placeholder when unconfigured so SlackNet registration does not
                    // NullReferenceException — SlackChannel.StartAsync fails the channel
                    // loud and degrades before this client is ever used.
                    c.UseApiToken(options.BotToken.IsNullOrEmpty()
                        ? "unconfigured"
                        : options.BotToken.Value);

                    if (options.SocketMode)
                        c.UseAppLevelToken(options.AppToken.IsNullOrEmpty()
                            ? "unconfigured"
                            : options.AppToken.Value);

                    c.RegisterEventHandler<MessageEvent, SlackChannel>();
                    c.RegisterEventHandler<AppMention, SlackChannel>();
                    c.ReplaceBlockActionHandling(context =>
                        context.ServiceProvider().GetRequiredService<SlackApprovalHandler>());
                });
            })
            // User lookup is exposed through the generic lookup_channel_user tool.
            // The gateway actor ref and default channel ID are resolved lazily via
            // SlackChannel since they're not available until StartAsync completes.
            .WithProactiveSendClient((sp, options) =>
            {
                var outbound = sp.GetRequiredService<ISlackOutboundClient>();
                var channel = sp.GetRequiredService<SlackChannel>();
                return new SlackProactiveOutboundClient(
                    outbound,
                    options,
                    () => channel.DefaultChannelId,
                    () => channel.Gateway);
            })
            .WithLookupTool((sp, options) =>
            {
                var slackApi = sp.GetRequiredService<SlackNet.ISlackApiClient>();
                var timeProvider = sp.GetRequiredService<TimeProvider>();
                return new LookupSlackUserTool(slackApi.Users, options, timeProvider);
            });
    }

    internal static void AddDiscordChannel(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRemoteChatChannel<DiscordChannel, DiscordChannelOptions>(
                ChannelType.Discord,
                configuration,
                new HashSet<ChannelOutputEffectKind> { ChannelOutputEffectKind.ProcessingIndicator })
            // Token validity is NOT checked here: an exception thrown from this
            // registration path aborts host construction and crashes the daemon.
            // A missing/invalid token is handled as a contained channel failure in
            // DiscordChannel.StartAsync instead (see issue #1033).
            .WithServices((channelServices, _) =>
                channelServices.AddSingleton(_ => new DiscordSocketClient(new DiscordSocketConfig
                {
                    GatewayIntents = global::Discord.GatewayIntents.Guilds
                        | global::Discord.GatewayIntents.GuildMessages
                        | global::Discord.GatewayIntents.DirectMessages
                        | global::Discord.GatewayIntents.MessageContent,
                    AlwaysDownloadUsers = false,
                    MessageCacheSize = 100
                })))
            .WithFilesHttpClient()
            .WithTransport<IDiscordGatewayClient, DiscordNetGatewayClient>()
            .WithReplyClient<IDiscordReplyClient, DiscordNetReplyClient>()
            .WithOutboundClient<IDiscordOutboundClient, DiscordNetOutboundClient>()
            .WithLookupClient<IDiscordAddressLookupClient, DiscordNetAddressLookupClient>()
            .WithRenderer<DiscordProcessingOutputRenderer>()
            .WithThreadHistory((sp, options) =>
            {
                var client = sp.GetRequiredService<DiscordSocketClient>();
                var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
                var contentScanner = sp.GetRequiredService<IContentScanner>();
                var toolConfig = sp.GetRequiredService<ToolConfig>();
                var modelCapabilities = sp.GetRequiredService<ModelCapabilities>();
                var paths = sp.GetRequiredService<NetclawPaths>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<DiscordThreadHistoryFetcher>();

                return new DiscordThreadHistoryFetcher(
                    client,
                    options,
                    httpFactory.CreateClient("discord-files"),
                    contentScanner,
                    toolConfig.AudienceProfiles,
                    modelCapabilities,
                    paths,
                    logger);
            })
            .WithReminderResolver<DiscordReminderTargetResolver>()
            .WithResolver((sp, options) => new DiscordAddressResolver(
                sp.GetRequiredService<IDiscordAddressLookupClient>(),
                options,
                () => string.IsNullOrWhiteSpace(options.DefaultChannelId)
                    ? null
                    : new DiscordChannelId(options.DefaultChannelId)))
            // The gateway actor ref is resolved lazily via DiscordChannel since it
            // is not available until StartAsync completes.
            .WithProactiveSendClient((sp, options) =>
            {
                var outbound = sp.GetRequiredService<IDiscordOutboundClient>();
                var channel = sp.GetRequiredService<DiscordChannel>();
                return new DiscordProactiveOutboundClient(
                    outbound,
                    options,
                    () => channel.Gateway);
            });
    }

    internal static void AddMattermostChannel(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRemoteChatChannel<MattermostChannel, MattermostChannelOptions>(ChannelType.Mattermost, configuration)
            // Token and server-URL validity are NOT checked here: an exception
            // thrown from this registration path aborts host construction and
            // crashes the daemon. A missing/invalid token or URL is handled as a
            // contained channel failure in MattermostChannel.StartAsync instead
            // (see issue #1033). The fallback values below are only ever
            // materialized for a misconfigured channel, which degrades before the
            // transport client is used.
            .WithServices((channelServices, options) =>
            {
                var serverUrl = MattermostServerUrl(options);
                var botToken = MattermostBotToken(options);
                channelServices.AddSingleton(_ => new MattermostClient(serverUrl, botToken));

                if (!string.IsNullOrEmpty(options.CallbackUrl))
                    channelServices.AddSingleton(new MattermostCallbackActionStore(TimeProvider.System));
            })
            .WithFilesHttpClient((options, client) =>
            {
                var botToken = MattermostBotToken(options);
                if (!string.IsNullOrEmpty(botToken))
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", botToken);
            })
            .WithTransport<IMattermostGatewayClient, MattermostNetGatewayClient>()
            .WithReplyClient<IMattermostReplyClient, MattermostNetReplyClient>()
            .WithThreadHistory((sp, options) =>
            {
                var client = sp.GetRequiredService<MattermostClient>();
                var contentScanner = sp.GetRequiredService<IContentScanner>();
                var toolConfig = sp.GetRequiredService<ToolConfig>();
                var modelCapabilities = sp.GetRequiredService<ModelCapabilities>();
                var paths = sp.GetRequiredService<NetclawPaths>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<MattermostThreadHistoryFetcher>();

                var gatewayClient = sp.GetRequiredService<IMattermostGatewayClient>();

                return new MattermostThreadHistoryFetcher(
                    client,
                    contentScanner,
                    options,
                    MattermostServerUrl(options),
                    () => gatewayClient.BotUserId?.Value,
                    toolConfig.AudienceProfiles,
                    modelCapabilities,
                    paths,
                    logger);
            })
            .WithReminderResolver<MattermostReminderTargetResolver>()
            .WithOutboundClient<IMattermostOutboundClient, MattermostNetOutboundClient>()
            .WithResolver((_, options) => new MattermostDestinationAddressResolver(
                options,
                () => string.IsNullOrWhiteSpace(options.DefaultChannelId)
                    ? null
                    : new MattermostChannelId(options.DefaultChannelId)))
            // The gateway actor ref and default channel ID are resolved lazily via
            // MattermostChannel since they're not available until StartAsync completes.
            .WithProactiveSendClient((sp, options) =>
            {
                var outbound = sp.GetRequiredService<IMattermostOutboundClient>();
                var channel = sp.GetRequiredService<MattermostChannel>();
                return new MattermostProactiveOutboundClient(
                    outbound,
                    options,
                    () => channel.DefaultChannelId,
                    () => channel.Gateway);
            })
            .WithLookupTool((sp, options) => new LookupMattermostUserTool(
                () => sp.GetRequiredService<MattermostClient>(),
                options));
    }

    private static string MattermostServerUrl(MattermostChannelOptions options)
        => string.IsNullOrWhiteSpace(options.ServerUrl)
            ? "https://mattermost.invalid"
            : options.ServerUrl;

    private static string MattermostBotToken(MattermostChannelOptions options)
        => options.BotToken?.Value ?? string.Empty;
}
