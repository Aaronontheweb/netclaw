// -----------------------------------------------------------------------
// <copyright file="ChannelRegistryRegistrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ChannelRegistryRegistrationTests
{
    [Fact]
    public void Registry_enumerates_output_capable_channels_only()
    {
        var descriptors = BuildDescriptors(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Slack:AllowDirectMessages"] = "true",
            ["Discord:Enabled"] = "true",
            ["Discord:AllowDirectMessages"] = "true",
            ["Mattermost:Enabled"] = "true",
            ["Mattermost:AllowDirectMessages"] = "true"
        });

        Assert.Equal(
            new[] { "discord", "mattermost", "slack", "tui" },
            descriptors.Keys.Order(StringComparer.Ordinal));

        Assert.DoesNotContain("headless", descriptors.Keys);
        Assert.DoesNotContain("signalr", descriptors.Keys);
        Assert.DoesNotContain("reminder", descriptors.Keys);
        Assert.DoesNotContain("webhook", descriptors.Keys);

        Assert.Equal(ChannelKind.RemoteChat, descriptors["slack"].Kind);
        Assert.Equal(ChannelKind.RemoteChat, descriptors["discord"].Kind);
        Assert.Equal(ChannelKind.RemoteChat, descriptors["mattermost"].Kind);
        Assert.Equal(ChannelKind.LocalInteractiveClient, descriptors["tui"].Kind);

        Assert.Equal(ChannelType.Tui, descriptors["tui"].ChannelType);
        Assert.NotEqual(ChannelType.SignalR, descriptors["tui"].ChannelType);

        foreach (var key in new[] { "slack", "discord", "mattermost" })
        {
            var descriptor = descriptors[key];
            Assert.True(descriptor.IsEnabled);
            Assert.True(descriptor.Capabilities.HasFlag(ChannelCapabilities.ReceiveMessages));
            Assert.True(descriptor.Capabilities.HasFlag(ChannelCapabilities.SendMessages));
            Assert.True(descriptor.Capabilities.HasFlag(ChannelCapabilities.RuntimeHealth));
            Assert.Contains(ChannelAddressKind.Destination, descriptor.AddressKinds);
            Assert.Contains(ChannelOutputEffectKind.TextMessage, descriptor.SupportedOutputEffects);
            Assert.Contains(ChannelToolIntentKind.SendMessage, descriptor.ToolIntents);
        }

        Assert.True(descriptors["slack"].Capabilities.HasFlag(ChannelCapabilities.DirectMessages));
        Assert.True(descriptors["mattermost"].Capabilities.HasFlag(ChannelCapabilities.DirectMessages));
        Assert.False(descriptors["discord"].Capabilities.HasFlag(ChannelCapabilities.DirectMessages));

        Assert.Contains(ChannelAddressKind.DirectMessage, descriptors["slack"].AddressKinds);
        Assert.Contains(ChannelAddressKind.DirectMessage, descriptors["mattermost"].AddressKinds);
        Assert.DoesNotContain(ChannelAddressKind.DirectMessage, descriptors["discord"].AddressKinds);

        Assert.True(descriptors["slack"].Capabilities.HasFlag(ChannelCapabilities.FileEgress));
        Assert.False(descriptors["discord"].Capabilities.HasFlag(ChannelCapabilities.FileEgress));
        Assert.False(descriptors["mattermost"].Capabilities.HasFlag(ChannelCapabilities.FileEgress));

        Assert.Contains(ChannelOutputEffectKind.FileAttachment, descriptors["slack"].SupportedOutputEffects);
        Assert.DoesNotContain(ChannelOutputEffectKind.FileAttachment, descriptors["discord"].SupportedOutputEffects);
        Assert.DoesNotContain(ChannelOutputEffectKind.FileAttachment, descriptors["mattermost"].SupportedOutputEffects);
    }

    [Fact]
    public void Disabled_remote_channels_still_have_disabled_descriptors()
    {
        var descriptors = BuildDescriptors(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "false",
            ["Discord:Enabled"] = "false",
            ["Mattermost:Enabled"] = "false"
        });

        Assert.False(descriptors["slack"].IsEnabled);
        Assert.False(descriptors["discord"].IsEnabled);
        Assert.False(descriptors["mattermost"].IsEnabled);
        Assert.True(descriptors["tui"].IsEnabled);
    }

    [Fact]
    public async Task Registry_returns_runtime_snapshots_for_registered_descriptors()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "false",
            ["Discord:Enabled"] = "false",
            ["Mattermost:Enabled"] = "false"
        });

        var registry = provider.GetRequiredService<IChannelRegistry>();

        var slack = await registry.GetSnapshotAsync(
            ChannelDescriptorKey.FromChannelType(ChannelType.Slack),
            TestContext.Current.CancellationToken);
        Assert.False(slack.IsEnabled);
        Assert.Equal(ChannelHealthStatus.Degraded, slack.Health);
        Assert.Equal("Slack connector is disabled in configuration.", slack.HealthDetail);

        var tui = await registry.GetSnapshotAsync(
            ChannelDescriptorKey.FromChannelType(ChannelType.Tui),
            TestContext.Current.CancellationToken);
        Assert.True(tui.IsEnabled);
        Assert.Equal(ChannelHealthStatus.Healthy, tui.Health);
        Assert.True(tui.IsReady);
    }

    [Fact]
    public void Registry_fails_loudly_on_duplicate_descriptor_keys()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var descriptor = new ChannelDescriptor(
            key,
            ChannelType.Slack,
            ChannelKind.RemoteChat,
            "Slack",
            IsEnabled: true,
            ChannelCapabilities.SendMessages,
            ToolIntents: new HashSet<ChannelToolIntentKind>(),
            AddressKinds: new HashSet<ChannelAddressKind>(),
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>());

        var providers = new IChannelDescriptorProvider[]
        {
            new StaticChannelDescriptorProvider(descriptor),
            new StaticChannelDescriptorProvider(descriptor)
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ChannelRegistry(providers, Array.Empty<IChannelRuntimeSnapshotProvider>()));

        Assert.Contains("Duplicate channel descriptor key 'slack'", ex.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, ChannelDescriptor> BuildDescriptors(
        IReadOnlyDictionary<string, string?> settings)
    {
        using var provider = BuildProvider(settings);
        return provider.GetRequiredService<IChannelRegistry>()
            .ListChannels()
            .ToDictionary(descriptor => descriptor.Key.Value, StringComparer.Ordinal);
    }

    private static ServiceProvider BuildProvider(IReadOnlyDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChannelRegistry();
        services.AddTuiChannelDescriptor();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        services.AddSlackChannelIntegration(configuration);
        services.AddDiscordChannelIntegration(configuration);
        services.AddMattermostChannelIntegration(configuration);

        return services.BuildServiceProvider();
    }
}
