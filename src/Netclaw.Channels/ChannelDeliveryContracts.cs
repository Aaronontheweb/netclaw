// -----------------------------------------------------------------------
// <copyright file="ChannelDeliveryContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netclaw.Actors.Channels;

namespace Netclaw.Channels;

public readonly record struct ChannelDescriptorKey
{
    public ChannelDescriptorKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public static ChannelDescriptorKey Create(string value)
    {
        return new ChannelDescriptorKey(value);
    }

    public static ChannelDescriptorKey FromChannelType(ChannelType channelType)
    {
        return Create(channelType.ToWireValue());
    }

    public override string ToString() => Value;
}

public enum ChannelKind
{
    RemoteChat,
    LocalInteractiveClient
}

[Flags]
public enum ChannelCapabilities
{
    None = 0,
    ReceiveMessages = 1 << 0,
    SendMessages = 1 << 1,
    DirectMessages = 1 << 2,
    ThreadedConversations = 1 << 3,
    InteractiveApproval = 1 << 4,
    FileIngress = 1 << 5,
    FileEgress = 1 << 6,
    ProactiveSend = 1 << 7,
    UserLookup = 1 << 8,
    DestinationLookup = 1 << 9,
    RuntimeHealth = 1 << 10
}

public enum ChannelAddressKind
{
    Destination,
    User,
    Thread,
    DirectMessage,
    LocalSession
}

public enum ChannelToolIntentKind
{
    SendMessage,
    LookupUser,
    LookupDestination
}

public enum ChannelOutputEffectKind
{
    TextMessage,
    MessageUpdate,
    InteractiveApproval,
    FileAttachment,
    ProcessingIndicator,
    Reaction,
    ThreadRename
}

public sealed record ChannelDescriptor(
    ChannelDescriptorKey Key,
    ChannelType ChannelType,
    ChannelKind Kind,
    string DisplayName,
    bool IsEnabled,
    ChannelCapabilities Capabilities,
    IReadOnlySet<ChannelToolIntentKind> ToolIntents,
    IReadOnlySet<ChannelAddressKind> AddressKinds,
    IReadOnlySet<ChannelOutputEffectKind> SupportedOutputEffects);

public sealed record ResolvedChannelAddress
{
    public ResolvedChannelAddress(
        ChannelDescriptorKey channelKey,
        ChannelAddressKind addressKind,
        string stableId,
        string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        ChannelKey = channelKey;
        AddressKind = addressKind;
        StableId = stableId;
        DisplayName = displayName;
    }

    public ChannelDescriptorKey ChannelKey { get; init; }

    public ChannelAddressKind AddressKind { get; init; }

    public string StableId { get; init; }

    public string DisplayName { get; init; }
}

public sealed record ChannelDeliveryTarget
{
    public ChannelDeliveryTarget(
        ChannelDescriptorKey channelKey,
        ResolvedChannelAddress destination,
        string? threadOrRootId = null)
    {
        if (!destination.ChannelKey.Equals(channelKey))
        {
            throw new ArgumentException(
                $"Destination channel key '{destination.ChannelKey}' does not match delivery target channel key '{channelKey}'.",
                nameof(destination));
        }

        ChannelKey = channelKey;
        Destination = destination;
        ThreadOrRootId = threadOrRootId;
    }

    public ChannelDescriptorKey ChannelKey { get; init; }

    public ResolvedChannelAddress Destination { get; init; }

    public string? ThreadOrRootId { get; init; }
}

public sealed record ChannelPrincipal(
    string StableId,
    string? DisplayName = null);

public sealed record ChannelActivitySnapshot(
    DateTimeOffset? LastInputAtUtc = null,
    DateTimeOffset? LastOutputAtUtc = null,
    long? InputCount = null,
    long? OutputCount = null);

public sealed record ChannelRuntimeSnapshot(
    ChannelDescriptorKey Key,
    bool IsEnabled,
    ChannelHealthStatus Health,
    string? HealthDetail = null,
    bool? IsConnected = null,
    bool? IsReady = null,
    ChannelPrincipal? Principal = null,
    ChannelActivitySnapshot? Activity = null);

public interface IChannelDescriptorProvider
{
    ChannelDescriptor GetDescriptor();
}

public interface IChannelRuntimeSnapshotProvider
{
    ChannelDescriptorKey Key { get; }

    ValueTask<ChannelRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface IChannelRegistry
{
    IReadOnlyCollection<ChannelDescriptor> ListChannels();

    ChannelDescriptor GetChannel(ChannelDescriptorKey key);

    ValueTask<ChannelRuntimeSnapshot> GetSnapshotAsync(
        ChannelDescriptorKey key,
        CancellationToken cancellationToken = default);
}

public sealed class ChannelRegistry : IChannelRegistry
{
    private readonly IReadOnlyDictionary<ChannelDescriptorKey, ChannelDescriptor> _descriptors;
    private readonly IReadOnlyDictionary<ChannelDescriptorKey, IChannelRuntimeSnapshotProvider> _snapshotProviders;

    public ChannelRegistry(
        IEnumerable<IChannelDescriptorProvider> descriptorProviders,
        IEnumerable<IChannelRuntimeSnapshotProvider> snapshotProviders)
    {
        ArgumentNullException.ThrowIfNull(descriptorProviders);
        ArgumentNullException.ThrowIfNull(snapshotProviders);

        _descriptors = BuildDescriptorLookup(descriptorProviders);
        _snapshotProviders = BuildSnapshotProviderLookup(snapshotProviders);
    }

    public IReadOnlyCollection<ChannelDescriptor> ListChannels()
    {
        return _descriptors.Values
            .OrderBy(descriptor => descriptor.Key.Value, StringComparer.Ordinal)
            .ToArray();
    }

    public ChannelDescriptor GetChannel(ChannelDescriptorKey key)
    {
        if (_descriptors.TryGetValue(key, out var descriptor))
            return descriptor;

        throw new InvalidOperationException($"No channel descriptor is registered for key '{key}'.");
    }

    public async ValueTask<ChannelRuntimeSnapshot> GetSnapshotAsync(
        ChannelDescriptorKey key,
        CancellationToken cancellationToken = default)
    {
        if (!_snapshotProviders.TryGetValue(key, out var provider))
            throw new InvalidOperationException($"No channel runtime snapshot provider is registered for key '{key}'.");

        return await provider.GetSnapshotAsync(cancellationToken);
    }

    private static IReadOnlyDictionary<ChannelDescriptorKey, ChannelDescriptor> BuildDescriptorLookup(
        IEnumerable<IChannelDescriptorProvider> providers)
    {
        var descriptors = new Dictionary<ChannelDescriptorKey, ChannelDescriptor>();

        foreach (var provider in providers)
        {
            var descriptor = provider.GetDescriptor();
            if (!descriptors.TryAdd(descriptor.Key, descriptor))
                throw new InvalidOperationException($"Duplicate channel descriptor key '{descriptor.Key}' registered.");
        }

        return descriptors;
    }

    private static IReadOnlyDictionary<ChannelDescriptorKey, IChannelRuntimeSnapshotProvider> BuildSnapshotProviderLookup(
        IEnumerable<IChannelRuntimeSnapshotProvider> providers)
    {
        var snapshotProviders = new Dictionary<ChannelDescriptorKey, IChannelRuntimeSnapshotProvider>();

        foreach (var provider in providers)
        {
            if (!snapshotProviders.TryAdd(provider.Key, provider))
                throw new InvalidOperationException($"Duplicate channel runtime snapshot provider key '{provider.Key}' registered.");
        }

        return snapshotProviders;
    }
}

public sealed class StaticChannelDescriptorProvider(ChannelDescriptor descriptor) : IChannelDescriptorProvider
{
    public ChannelDescriptor GetDescriptor() => descriptor;
}

public static class ChannelRegistryServiceCollectionExtensions
{
    public static IServiceCollection AddChannelRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IChannelRegistry, ChannelRegistry>();
        return services;
    }

    public static IServiceCollection AddChannelDescriptor(
        this IServiceCollection services,
        ChannelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);

        services.AddSingleton<IChannelDescriptorProvider>(new StaticChannelDescriptorProvider(descriptor));
        return services;
    }

    public static IServiceCollection AddTuiChannelDescriptor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddChannelDescriptor(new ChannelDescriptor(
            ChannelDescriptorKey.FromChannelType(ChannelType.Tui),
            ChannelType.Tui,
            ChannelKind.LocalInteractiveClient,
            "TUI",
            IsEnabled: true,
            ChannelCapabilities.ReceiveMessages
                | ChannelCapabilities.SendMessages
                | ChannelCapabilities.InteractiveApproval
                | ChannelCapabilities.FileEgress
                | ChannelCapabilities.RuntimeHealth,
            ToolIntents: new HashSet<ChannelToolIntentKind>(),
            AddressKinds: new HashSet<ChannelAddressKind> { ChannelAddressKind.LocalSession },
            SupportedOutputEffects: new HashSet<ChannelOutputEffectKind>
            {
                ChannelOutputEffectKind.TextMessage,
                ChannelOutputEffectKind.InteractiveApproval,
                ChannelOutputEffectKind.FileAttachment
            }));
    }
}
