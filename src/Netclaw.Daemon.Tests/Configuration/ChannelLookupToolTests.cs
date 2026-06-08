// -----------------------------------------------------------------------
// <copyright file="ChannelLookupToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Daemon.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ChannelLookupToolTests
{
    [Fact]
    public void Registration_skips_generic_lookup_tools_when_remote_channels_are_disabled()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "false",
            ["Discord:Enabled"] = "false",
            ["Mattermost:Enabled"] = "false"
        });

        Assert.False(IsRegistered<LookupChannelUserTool>(services));
        Assert.False(IsRegistered<LookupChannelDestinationTool>(services));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IChannelTool));
    }

    [Fact]
    public void Registration_adds_user_and_destination_for_discord_only_configuration()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "false",
            ["Discord:Enabled"] = "true",
            ["Mattermost:Enabled"] = "false"
        });

        Assert.True(IsRegistered<LookupChannelUserTool>(services));
        Assert.True(IsRegistered<LookupChannelDestinationTool>(services));
        Assert.Equal(2, services.Count(descriptor => descriptor.ServiceType == typeof(IChannelTool)));
    }

    [Fact]
    public void Registration_adds_user_and_destination_for_user_lookup_channels()
    {
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Slack:Enabled"] = "true",
            ["Discord:Enabled"] = "false",
            ["Mattermost:Enabled"] = "false"
        });

        Assert.True(IsRegistered<LookupChannelUserTool>(services));
        Assert.True(IsRegistered<LookupChannelDestinationTool>(services));
        Assert.Equal(2, services.Count(descriptor => descriptor.ServiceType == typeof(IChannelTool)));
    }

    [Fact]
    public void User_lookup_schema_enumerates_enabled_user_channels_only()
    {
        var registry = BuildRegistry(
            BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.User),
            BuildDescriptor(ChannelType.Mattermost, isEnabled: false, ChannelAddressKind.User),
            BuildDescriptor(ChannelType.Discord, isEnabled: true, ChannelAddressKind.Destination));
        var tool = new LookupChannelUserTool(registry);

        var keys = ReadChannelKeyEnum(tool.ParameterSchema);

        Assert.Equal(["slack"], keys);
    }

    [Fact]
    public void Destination_lookup_schema_enumerates_enabled_destination_channels()
    {
        var registry = BuildRegistry(
            BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.Destination),
            BuildDescriptor(ChannelType.Mattermost, isEnabled: true, ChannelAddressKind.Destination),
            BuildDescriptor(ChannelType.Discord, isEnabled: true, ChannelAddressKind.Destination),
            BuildDescriptor(ChannelType.Tui, isEnabled: true, ChannelAddressKind.LocalSession));
        var tool = new LookupChannelDestinationTool(registry);

        var keys = ReadChannelKeyEnum(tool.ParameterSchema);

        Assert.Equal(["discord", "mattermost", "slack"], keys);
    }

    [Fact]
    public async Task User_lookup_routes_to_registered_channel_resolver()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var address = new ResolvedChannelAddress(key, ChannelAddressKind.User, "U123", "Alice Smith");
        var resolver = new TestAddressResolver(key, ChannelAddressKind.User)
        {
            Result = ChannelAddressResolutionResult.Resolved(address)
        };
        var registry = BuildRegistry(
            [BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.User)],
            [resolver]);
        var tool = new LookupChannelUserTool(registry);

        var result = await ExecuteAsync(tool, "slack", "alice");

        Assert.Contains("Resolved user on channel 'slack'", result);
        Assert.Contains("channel_key: slack", result);
        Assert.Contains("stable_id: U123", result);
        Assert.Contains("display_name: Alice Smith", result);
        Assert.Contains("address_kind: user", result);
        Assert.Equal("alice", resolver.Request?.Query);
    }

    [Fact]
    public async Task Destination_lookup_formats_ambiguous_candidates()
    {
        var key = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);
        var resolver = new TestAddressResolver(key, ChannelAddressKind.Destination)
        {
            Result = ChannelAddressResolutionResult.Ambiguous(
            [
                new ResolvedChannelAddress(key, ChannelAddressKind.Destination, "C1", "#general"),
                new ResolvedChannelAddress(key, ChannelAddressKind.Destination, "C2", "#general-private")
            ],
            "Multiple destinations matched.")
        };
        var registry = BuildRegistry(
            [BuildDescriptor(ChannelType.Slack, isEnabled: true, ChannelAddressKind.Destination)],
            [resolver]);
        var tool = new LookupChannelDestinationTool(registry);

        var result = await ExecuteAsync(tool, "slack", "general");

        Assert.Contains("Ambiguous destination lookup", result);
        Assert.Contains("channel_key: slack", result);
        Assert.Contains("stable_id: C1", result);
        Assert.Contains("stable_id: C2", result);
        Assert.Contains("address_kind: destination", result);
        Assert.Contains("Multiple destinations matched.", result);
    }

    [Fact]
    public async Task Lookup_rejects_disabled_channel_descriptor()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Slack, isEnabled: false, ChannelAddressKind.User));
        var tool = new LookupChannelUserTool(registry);

        var result = await ExecuteAsync(tool, "slack", "alice");

        Assert.Contains("Channel 'slack' is disabled", result);
    }

    [Fact]
    public async Task Destination_lookup_reports_missing_discord_resolver()
    {
        var registry = BuildRegistry(BuildDescriptor(ChannelType.Discord, isEnabled: true, ChannelAddressKind.Destination));
        var tool = new LookupChannelDestinationTool(registry);

        var result = await ExecuteAsync(tool, "discord", "general");

        Assert.Contains("No channel address resolver is registered for key 'discord'", result);
    }

    private static Task<string> ExecuteAsync(ChannelLookupTool tool, string channelKey, string query)
    {
        return tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["channel_key"] = channelKey,
            ["query"] = query,
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

    private static ServiceCollection BuildServices(IReadOnlyDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        services.AddChannelLookupTools(configuration);
        return services;
    }

    private static bool IsRegistered<T>(IServiceCollection services)
    {
        return services.Any(descriptor => descriptor.ServiceType == typeof(T));
    }

    private static ChannelRegistry BuildRegistry(params ChannelDescriptor[] descriptors)
    {
        return BuildRegistry(descriptors, []);
    }

    private static ChannelRegistry BuildRegistry(
        IReadOnlyList<ChannelDescriptor> descriptors,
        IReadOnlyList<IChannelAddressResolver> resolvers)
    {
        var providers = descriptors.Select(descriptor => new StaticChannelDescriptorProvider(descriptor)).ToArray();
        return new ChannelRegistry(providers, [], resolvers);
    }

    private static ChannelDescriptor BuildDescriptor(
        ChannelType channelType,
        bool isEnabled,
        params ChannelAddressKind[] addressKinds)
    {
        return new ChannelDescriptor(
            ChannelDescriptorKey.FromChannelType(channelType),
            channelType,
            channelType == ChannelType.Tui ? ChannelKind.LocalInteractiveClient : ChannelKind.RemoteChat,
            channelType.ToString(),
            isEnabled,
            ChannelCapabilities.SendMessages,
            ToolIntents: new HashSet<ChannelToolIntentKind>(),
            AddressKinds: new HashSet<ChannelAddressKind>(addressKinds),
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>());
    }

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
