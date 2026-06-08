// -----------------------------------------------------------------------
// <copyright file="DiscordChannelRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Discord.WebSocket;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Reminders;
using Netclaw.Channels;
using Netclaw.Channels.Discord;
using Netclaw.Channels.Discord.Tools;
using Netclaw.Channels.Discord.Transport;
using Netclaw.Configuration;
using Netclaw.Configuration.Http;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Daemon.Configuration;

public static class DiscordChannelRegistrationExtensions
{
    private const string DiscordChannelKey = "discord";

    public static void AddDiscordChannelIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        var discordOptions = configuration.GetSection("Discord").Get<DiscordChannelOptions>() ?? new DiscordChannelOptions();
        services.AddSingleton(discordOptions);
        services.AddChannelRegistry();
        services.AddChannelDescriptorWithRuntimeSnapshot(CreateDescriptor(discordOptions));

        if (!discordOptions.Enabled)
            return;

        // Token validity is NOT checked here: an exception thrown from this
        // registration path aborts host construction and crashes the daemon.
        // A missing/invalid token is handled as a contained channel failure in
        // DiscordChannel.StartAsync instead (see issue #1033).
        services.AddSingleton(_ => new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = Discord.GatewayIntents.Guilds
                | Discord.GatewayIntents.GuildMessages
                | Discord.GatewayIntents.DirectMessages
                | Discord.GatewayIntents.MessageContent,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 100
        }));

        services.AddHttpClient("discord-files").AddNetclawHeaders("discord-files");
        services.AddSingleton<IDiscordGatewayClient, DiscordNetGatewayClient>();
        services.AddSingleton<IDiscordReplyClient, DiscordNetReplyClient>();
        services.AddSingleton<IDiscordOutboundClient, DiscordNetOutboundClient>();
        services.AddSingleton<IDiscordAddressLookupClient, DiscordNetAddressLookupClient>();
        services.AddChannelOutputRenderer<DiscordProcessingOutputRenderer>();
        services.AddSingleton<IThreadHistoryFetcher>(sp =>
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
                discordOptions,
                httpFactory.CreateClient("discord-files"),
                contentScanner,
                toolConfig.AudienceProfiles,
                modelCapabilities,
                paths,
                logger);
        });
        services.AddSingleton<IReminderTargetResolver, DiscordReminderTargetResolver>();
        services.AddSingleton<DiscordAddressResolver>(sp => new DiscordAddressResolver(
            sp.GetRequiredService<IDiscordAddressLookupClient>(),
            discordOptions,
            () => string.IsNullOrWhiteSpace(discordOptions.DefaultChannelId)
                ? null
                : new DiscordChannelId(discordOptions.DefaultChannelId)));
        services.AddSingleton<IChannelAddressResolver>(sp => sp.GetRequiredService<DiscordAddressResolver>());

        services.AddKeyedSingleton<IChannel, DiscordChannel>(DiscordChannelKey);
        services.AddSingleton<IChannel>(sp =>
            sp.GetRequiredKeyedService<IChannel>(DiscordChannelKey));
        services.AddSingleton<DiscordChannel>(sp =>
            (DiscordChannel)sp.GetRequiredKeyedService<IChannel>(DiscordChannelKey));

        // Concrete send implementation used by send_channel_message.
        // The gateway actor ref is resolved lazily via DiscordChannel since it
        // is not available until StartAsync completes.
        services.AddSingleton<SendDiscordMessageTool>(sp =>
        {
            var outbound = sp.GetRequiredService<IDiscordOutboundClient>();
            var channel = sp.GetRequiredService<DiscordChannel>();
            return new SendDiscordMessageTool(
                outbound,
                discordOptions,
                () => channel.Gateway);
        });

        services.AddSingleton<IHostedService>(sp =>
            (IHostedService)sp.GetRequiredKeyedService<IChannel>(DiscordChannelKey));
    }

    private static ChannelDescriptor CreateDescriptor(DiscordChannelOptions options)
    {
        var capabilities = ChannelCapabilities.ReceiveMessages
            | ChannelCapabilities.SendMessages
            | ChannelCapabilities.ThreadedConversations
            | ChannelCapabilities.InteractiveApproval
            | ChannelCapabilities.FileIngress
            | ChannelCapabilities.FileEgress
            | ChannelCapabilities.ProactiveSend
            | ChannelCapabilities.UserLookup
            | ChannelCapabilities.DestinationLookup
            | ChannelCapabilities.RuntimeHealth;

        if (options.AllowDirectMessages)
            capabilities |= ChannelCapabilities.DirectMessages;

        var addressKinds = new HashSet<ChannelAddressKind>
        {
            ChannelAddressKind.Destination,
            ChannelAddressKind.User,
            ChannelAddressKind.Thread
        };

        if (options.AllowDirectMessages)
            addressKinds.Add(ChannelAddressKind.DirectMessage);

        return new ChannelDescriptor(
            ChannelDescriptorKey.FromChannelType(ChannelType.Discord),
            ChannelType.Discord,
            ChannelKind.RemoteChat,
            "Discord",
            options.Enabled,
            capabilities,
            ToolIntents: new HashSet<ChannelToolIntentKind>
            {
                ChannelToolIntentKind.SendMessage,
                ChannelToolIntentKind.LookupUser
            },
            AddressKinds: addressKinds,
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>
            {
                ChannelOutputEffectKind.TextMessage,
                ChannelOutputEffectKind.InteractiveApproval,
                ChannelOutputEffectKind.FileAttachment,
                ChannelOutputEffectKind.ProcessingIndicator
            });
    }
}
