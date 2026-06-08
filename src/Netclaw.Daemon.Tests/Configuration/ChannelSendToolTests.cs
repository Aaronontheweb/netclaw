// -----------------------------------------------------------------------
// <copyright file="ChannelSendToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ChannelSendToolTests
{
    [Fact]
    public void Send_schema_enumerates_enabled_send_channels_only()
    {
        var registry = BuildRegistry(
            BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination),
            BuildDescriptor(ChannelType.Discord, isEnabled: false, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination),
            BuildDescriptor(
                ChannelType.Tui,
                isEnabled: true,
                ChannelCapabilities.SendMessages,
                [ChannelAddressKind.LocalSession],
                includeSendIntent: false));
        var tool = new SendChannelMessageTool(registry, new ServiceCollection().BuildServiceProvider());

        var keys = ReadChannelKeyEnum(tool.ParameterSchema);

        Assert.Equal(["slack"], keys);
    }

    [Fact]
    public async Task Send_rejects_mismatched_destination_channel_key()
    {
        var registry = BuildRegistry(
            BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination),
            BuildDescriptor(ChannelType.Mattermost, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination));
        var tool = new SendChannelMessageTool(registry, new ServiceCollection().BuildServiceProvider());

        var result = await ExecuteAsync(tool, "mattermost", "slack", "destination", "C1234567890");

        Assert.Contains("destination.channel_key 'slack' does not match channel_key 'mattermost'", result);
    }

    [Fact]
    public async Task Send_rejects_unsupported_direct_message_capability()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Discord, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination));
        var tool = new SendChannelMessageTool(registry, new ServiceCollection().BuildServiceProvider());

        var result = await ExecuteAsync(tool, "discord", "discord", "direct_message", "123456789012345678");

        Assert.Contains("Channel 'discord' does not support direct-message output", result);
    }

    [Fact]
    public async Task Send_rejects_bare_display_name_destination()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelCapabilities.SendMessages, ChannelAddressKind.Destination));
        var tool = new SendChannelMessageTool(registry, new ServiceCollection().BuildServiceProvider());

        var result = await ExecuteAsync(tool, "slack", "slack", "destination", "#general");

        Assert.Contains("Bare display names", result);
    }

    [Fact]
    public async Task Send_rejects_user_kind_and_instructs_direct_message_workflow()
    {
        var registry = BuildRegistry(BuildDescriptor(
            ChannelType.Slack,
            isEnabled: true,
            ChannelCapabilities.SendMessages | ChannelCapabilities.DirectMessages,
            ChannelAddressKind.Destination,
            ChannelAddressKind.DirectMessage));
        var tool = new SendChannelMessageTool(registry, new ServiceCollection().BuildServiceProvider());

        var result = await ExecuteAsync(tool, "slack", "slack", "user", "U1234567890");

        Assert.Contains("cannot send to destination.kind 'user'", result);
        Assert.Contains("destination.kind='direct_message'", result);
    }

    private static Task<string> ExecuteAsync(
        SendChannelMessageTool tool,
        string channelKey,
        string destinationChannelKey,
        string destinationKind,
        string destinationId)
    {
        return tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["channel_key"] = channelKey,
            ["destination"] = new Dictionary<string, object?>
            {
                ["channel_key"] = destinationChannelKey,
                ["kind"] = destinationKind,
                ["id"] = destinationId
            },
            ["text"] = "Test message",
            ["_rationale"] = "test"
        }, TestContext.Current.CancellationToken);
    }

    private static string[] ReadChannelKeyEnum(JsonElement schema)
    {
        return schema
            .GetProperty("properties")
            .GetProperty("channel_key")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
    }

    private static ChannelRegistry BuildRegistry(params ChannelDescriptor[] descriptors)
    {
        var providers = descriptors.Select(descriptor => new StaticChannelDescriptorProvider(descriptor)).ToArray();
        return new ChannelRegistry(providers, []);
    }

    private static ChannelDescriptor BuildDescriptor(
        ChannelType channelType,
        bool isEnabled,
        ChannelCapabilities capabilities,
        params ChannelAddressKind[] addressKinds)
    {
        return BuildDescriptor(channelType, isEnabled, capabilities, addressKinds, includeSendIntent: true);
    }

    private static ChannelDescriptor BuildDescriptor(
        ChannelType channelType,
        bool isEnabled,
        ChannelCapabilities capabilities,
        ChannelAddressKind[] addressKinds,
        bool includeSendIntent)
    {
        var toolIntents = includeSendIntent
            ? new HashSet<ChannelToolIntentKind> { ChannelToolIntentKind.SendMessage }
            : new HashSet<ChannelToolIntentKind>();

        return new ChannelDescriptor(
            ChannelDescriptorKey.FromChannelType(channelType),
            channelType,
            channelType == ChannelType.Tui ? ChannelKind.LocalInteractiveClient : ChannelKind.RemoteChat,
            channelType.ToString(),
            isEnabled,
            capabilities,
            ToolIntents: toolIntents,
            AddressKinds: new HashSet<ChannelAddressKind>(addressKinds),
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>());
    }
}
