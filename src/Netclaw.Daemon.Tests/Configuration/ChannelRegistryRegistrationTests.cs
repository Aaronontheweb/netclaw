// -----------------------------------------------------------------------
// <copyright file="ChannelRegistryRegistrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Channels.Discord.Tools;
using Netclaw.Channels.Mattermost.Tools;
using Netclaw.Channels.Slack.Tools;
using Netclaw.Daemon.Configuration;
using Netclaw.Tools;
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
    public void Disabled_remote_channels_do_not_register_channel_tools()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "false",
            ["Discord:Enabled"] = "false",
            ["Mattermost:Enabled"] = "false"
        });

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IChannelTool));
        Assert.False(IsRegistered<SendSlackMessageTool>(services));
        Assert.False(IsRegistered<LookupSlackUserTool>(services));
        Assert.False(IsRegistered<SendDiscordMessageTool>(services));
        Assert.False(IsRegistered<SendMattermostMessageTool>(services));
        Assert.False(IsRegistered<LookupMattermostUserTool>(services));
    }

    [Fact]
    public void Enabled_remote_channels_register_expected_channel_tools()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Discord:Enabled"] = "true",
            ["Mattermost:Enabled"] = "true"
        });

        Assert.Equal(5, services.Count(descriptor => descriptor.ServiceType == typeof(IChannelTool)));
        Assert.True(IsRegistered<SendSlackMessageTool>(services));
        Assert.True(IsRegistered<LookupSlackUserTool>(services));
        Assert.True(IsRegistered<SendDiscordMessageTool>(services));
        Assert.True(IsRegistered<SendMattermostMessageTool>(services));
        Assert.True(IsRegistered<LookupMattermostUserTool>(services));
    }

    [Fact]
    public void Enabled_channel_tool_intents_match_registered_tool_services()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Discord:Enabled"] = "true",
            ["Mattermost:Enabled"] = "true"
        };
        var services = BuildServices(settings);

        using var provider = BuildProvider(settings);
        var descriptors = provider.GetRequiredService<IChannelRegistry>()
            .ListChannels()
            .ToDictionary(descriptor => descriptor.Key.Value, StringComparer.Ordinal);

        AssertToolIntents(
            descriptors["slack"],
            services,
            new ChannelToolExpectation(ChannelToolIntentKind.SendMessage, typeof(SendSlackMessageTool), "send_slack_message"),
            new ChannelToolExpectation(ChannelToolIntentKind.LookupUser, typeof(LookupSlackUserTool), "lookup_slack_user"));
        AssertToolIntents(
            descriptors["discord"],
            services,
            new ChannelToolExpectation(ChannelToolIntentKind.SendMessage, typeof(SendDiscordMessageTool), "send_discord_message"));
        AssertToolIntents(
            descriptors["mattermost"],
            services,
            new ChannelToolExpectation(ChannelToolIntentKind.SendMessage, typeof(SendMattermostMessageTool), "send_mattermost_message"),
            new ChannelToolExpectation(ChannelToolIntentKind.LookupUser, typeof(LookupMattermostUserTool), "lookup_mattermost_user"));
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

    [Fact]
    public void Registry_fails_loudly_on_duplicate_address_resolvers()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var descriptor = BuildDescriptor(key, ChannelType.Slack, ChannelAddressKind.User);
        var resolver = new TestAddressResolver(key, ChannelAddressKind.User);

        var providers = new[] { new StaticChannelDescriptorProvider(descriptor) };
        var resolvers = new[] { resolver, resolver };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ChannelRegistry(providers, Array.Empty<IChannelRuntimeSnapshotProvider>(), resolvers));

        Assert.Contains(
            "Duplicate channel address resolver key 'slack' for address kind 'User' registered.",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_fails_loudly_when_address_kind_is_not_supported()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Discord);
        var descriptor = BuildDescriptor(key, ChannelType.Discord, ChannelAddressKind.Destination);
        var registry = new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            Array.Empty<IChannelRuntimeSnapshotProvider>(),
            Array.Empty<IChannelAddressResolver>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            registry.GetResolver(key, ChannelAddressKind.DirectMessage));

        Assert.Contains("Channel 'discord' does not support address kind 'DirectMessage'.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_fails_loudly_when_supported_address_kind_has_no_resolver()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);
        var descriptor = BuildDescriptor(key, ChannelType.Mattermost, ChannelAddressKind.User);
        var registry = new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            Array.Empty<IChannelRuntimeSnapshotProvider>(),
            Array.Empty<IChannelAddressResolver>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            registry.GetResolver(key, ChannelAddressKind.User));

        Assert.Contains(
            "No channel address resolver is registered for key 'mattermost' and address kind 'User'.",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registry_routes_resolution_requests_to_selected_channel_resolver()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var descriptor = BuildDescriptor(key, ChannelType.Slack, ChannelAddressKind.User, ChannelAddressKind.Destination);
        var userAddress = new ResolvedChannelAddress(key, ChannelAddressKind.User, "U123", "Jennifer Stannard");
        var destinationAddress = new ResolvedChannelAddress(key, ChannelAddressKind.Destination, "C123", "#general");
        var resolver = new TestAddressResolver(key, ChannelAddressKind.User, ChannelAddressKind.Destination)
        {
            Result = ChannelAddressResolutionResult.Ambiguous([userAddress, destinationAddress], "Multiple matches.")
        };
        var registry = new ChannelRegistry(
            [new StaticChannelDescriptorProvider(descriptor)],
            Array.Empty<IChannelRuntimeSnapshotProvider>(),
            [resolver]);
        var request = new ChannelAddressResolutionRequest(key, ChannelAddressKind.User, "jennifer", requireSingleMatch: false);

        var result = await registry.ResolveAddressAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(resolver.Result, result);
        Assert.Same(request, resolver.Request);
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
        return BuildServices(settings).BuildServiceProvider();
    }

    private static ServiceCollection BuildServices(IReadOnlyDictionary<string, string?> settings)
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

        return services;
    }

    private static bool IsRegistered<T>(IServiceCollection services)
    {
        return services.Any(descriptor => descriptor.ServiceType == typeof(T));
    }

    private static ChannelDescriptor BuildDescriptor(
        ChannelDescriptorKey key,
        ChannelType channelType,
        params ChannelAddressKind[] addressKinds)
    {
        return new ChannelDescriptor(
            key,
            channelType,
            ChannelKind.RemoteChat,
            channelType.ToString(),
            IsEnabled: true,
            ChannelCapabilities.SendMessages,
            ToolIntents: new HashSet<ChannelToolIntentKind>(),
            AddressKinds: new HashSet<ChannelAddressKind>(addressKinds),
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>());
    }

    private static void AssertToolIntents(
        ChannelDescriptor descriptor,
        IServiceCollection services,
        params ChannelToolExpectation[] expectedTools)
    {
        Assert.True(descriptor.IsEnabled);

        Assert.Equal(
            expectedTools.Select(tool => tool.Intent).Order().ToArray(),
            descriptor.ToolIntents.Order().ToArray());

        foreach (var expectedTool in expectedTools)
        {
            Assert.Contains(services, serviceDescriptor => serviceDescriptor.ServiceType == expectedTool.ToolType);

            var attribute = Assert.Single(expectedTool.ToolType.GetCustomAttributes(
                typeof(NetclawToolAttribute), inherit: false));
            Assert.Equal(expectedTool.ToolName, ((NetclawToolAttribute)attribute).Name);
        }
    }

    private sealed record ChannelToolExpectation(
        ChannelToolIntentKind Intent,
        Type ToolType,
        string ToolName);

    private sealed class TestAddressResolver(
        ChannelDescriptorKey key,
        params ChannelAddressKind[] addressKinds) : IChannelAddressResolver
    {
        public ChannelDescriptorKey Key { get; } = key;

        public IReadOnlySet<ChannelAddressKind> AddressKinds { get; } = new HashSet<ChannelAddressKind>(addressKinds);

        public ChannelAddressResolutionRequest? Request { get; private set; }

        public ChannelAddressResolutionResult Result { get; init; } = ChannelAddressResolutionResult.NotFound();

        public ValueTask<ChannelAddressResolutionResult> ResolveAsync(
            ChannelAddressResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(Result);
        }
    }
}
