// -----------------------------------------------------------------------
// <copyright file="SlackChannelRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Reminders;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;
using Netclaw.Configuration.Http;
using Netclaw.Channels.Slack.Tools;
using Netclaw.Tools;
using SlackNet.Events;
using SlackNet.Extensions.DependencyInjection;
using SlackNet.Interaction.Experimental;

namespace Netclaw.Daemon.Configuration;

public static class SlackChannelRegistrationExtensions
{
    private const string SlackChannelKey = "slack";

    public static void AddSlackChannelIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        var slackOptions = configuration.GetSection("Slack").Get<SlackChannelOptions>() ?? new SlackChannelOptions();
        services.AddSingleton(slackOptions);
        services.AddChannelRegistry();
        services.AddChannelDescriptorWithRuntimeSnapshot(CreateDescriptor(slackOptions));

        if (!slackOptions.Enabled)
            return;

        // Token validity is NOT checked here: an exception thrown from this
        // registration path aborts host construction and crashes the daemon.
        // A missing/invalid token is handled as a contained channel failure in
        // SlackChannel.StartAsync instead (see issue #1033).
        services.AddHttpClient("slack-files").AddNetclawHeaders("slack-files");
        services.AddSingleton<ISlackReplyClient, SlackReplyClient>();
        services.AddSingleton<IThreadHistoryFetcher>(sp =>
        {
            var slackApi = sp.GetRequiredService<SlackNet.ISlackApiClient>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var contentScanner = sp.GetRequiredService<Netclaw.Security.IContentScanner>();
            var paths = sp.GetRequiredService<NetclawPaths>();
            var toolConfig = sp.GetRequiredService<ToolConfig>();
            var modelCapabilities = sp.GetRequiredService<ModelCapabilities>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SlackThreadHistoryFetcher>();
            return new SlackThreadHistoryFetcher(
                slackApi.Conversations,
                slackOptions,
                httpFactory.CreateClient("slack-files"),
                contentScanner,
                paths,
                toolConfig.AudienceProfiles,
                modelCapabilities,
                logger);
        });
        services.AddSingleton<ISlackOutboundClient, SlackOutboundClient>();
        services.AddSingleton<ISlackTargetLookupClient, SlackApiTargetLookupClient>();
        services.AddSingleton<IReminderTargetResolver, SlackReminderTargetResolver>();
        services.AddSingleton<SlackApprovalHandler>();
        services.AddKeyedSingleton<IChannel, SlackChannel>(SlackChannelKey);
        services.AddSingleton<IChannel>(sp =>
            sp.GetRequiredKeyedService<IChannel>(SlackChannelKey));
        services.AddSingleton<SlackChannel>(sp =>
            (SlackChannel)sp.GetRequiredKeyedService<IChannel>(SlackChannelKey));
        services.AddSingleton<SlackTargetResolver>(sp =>
        {
            var lookup = sp.GetRequiredService<ISlackTargetLookupClient>();
            return new SlackTargetResolver(
                lookup,
                slackOptions,
                () => string.IsNullOrWhiteSpace(slackOptions.DefaultChannelId)
                    ? null
                    : new SlackChannelId(slackOptions.DefaultChannelId));
        });
        services.AddSingleton<ISlackTargetResolver>(sp => sp.GetRequiredService<SlackTargetResolver>());
        services.AddSingleton<IChannelAddressResolver>(sp => sp.GetRequiredService<SlackTargetResolver>());

        services.AddSlackNet(c =>
        {
            // Placeholder when unconfigured so SlackNet registration does not
            // NullReferenceException — SlackChannel.StartAsync fails the channel
            // loud and degrades before this client is ever used.
            c.UseApiToken(slackOptions.BotToken.IsNullOrEmpty()
                ? "unconfigured"
                : slackOptions.BotToken.Value);

            if (slackOptions.SocketMode)
                c.UseAppLevelToken(slackOptions.AppToken.IsNullOrEmpty()
                    ? "unconfigured"
                    : slackOptions.AppToken.Value);

            c.RegisterEventHandler<MessageEvent, SlackChannel>();
            c.RegisterEventHandler<AppMention, SlackChannel>();
            c.ReplaceBlockActionHandling(context =>
                context.ServiceProvider().GetRequiredService<SlackApprovalHandler>());
        });

        // Concrete send implementation used by send_channel_message.
        // User lookup is exposed through the generic lookup_channel_user tool.
        // The gateway actor ref and default channel ID are resolved lazily via
        // SlackChannel since they're not available until StartAsync completes.
        services.AddSingleton<SendSlackMessageTool>(sp =>
        {
            var outbound = sp.GetRequiredService<ISlackOutboundClient>();
            var channel = sp.GetRequiredService<SlackChannel>();
            return new SendSlackMessageTool(
                outbound,
                slackOptions,
                () => channel.DefaultChannelId,
                () => channel.Gateway);
        });

        services.AddSingleton<LookupSlackUserTool>(sp =>
        {
            var slackApi = sp.GetRequiredService<SlackNet.ISlackApiClient>();
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            return new LookupSlackUserTool(slackApi.Users, slackOptions, timeProvider);
        });
        services.AddSingleton<IChannelAddressResolver>(sp => sp.GetRequiredService<LookupSlackUserTool>());

        services.AddSingleton<IHostedService>(sp =>
            (IHostedService)sp.GetRequiredKeyedService<IChannel>(SlackChannelKey));
    }

    private static ChannelDescriptor CreateDescriptor(SlackChannelOptions options)
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
            ChannelDescriptorKey.FromChannelType(ChannelType.Slack),
            ChannelType.Slack,
            ChannelKind.RemoteChat,
            "Slack",
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
                ChannelOutputEffectKind.FileAttachment
            });
    }
}
