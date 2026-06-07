// -----------------------------------------------------------------------
// <copyright file="ChannelDeliveryContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Telemetry;

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

public sealed record ChannelAddressResolutionRequest
{
    public ChannelAddressResolutionRequest(
        ChannelDescriptorKey channelKey,
        ChannelAddressKind addressKind,
        string query,
        bool requireSingleMatch = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        ChannelKey = channelKey;
        AddressKind = addressKind;
        Query = query;
        RequireSingleMatch = requireSingleMatch;
    }

    public ChannelDescriptorKey ChannelKey { get; init; }

    public ChannelAddressKind AddressKind { get; init; }

    public string Query { get; init; }

    public bool RequireSingleMatch { get; init; }
}

public enum ChannelAddressResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    Unsupported
}

public sealed record ChannelAddressResolutionResult
{
    private ChannelAddressResolutionResult(
        ChannelAddressResolutionStatus status,
        IReadOnlyList<ResolvedChannelAddress> candidates,
        string? error = null)
    {
        Status = status;
        Candidates = candidates;
        Error = error;
    }

    public ChannelAddressResolutionStatus Status { get; init; }

    public IReadOnlyList<ResolvedChannelAddress> Candidates { get; init; }

    public string? Error { get; init; }

    public static ChannelAddressResolutionResult Resolved(ResolvedChannelAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new ChannelAddressResolutionResult(ChannelAddressResolutionStatus.Resolved, [address]);
    }

    public static ChannelAddressResolutionResult NotFound(string? error = null)
    {
        return new ChannelAddressResolutionResult(ChannelAddressResolutionStatus.NotFound, [], error);
    }

    public static ChannelAddressResolutionResult Ambiguous(IReadOnlyList<ResolvedChannelAddress> candidates, string? error = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return new ChannelAddressResolutionResult(ChannelAddressResolutionStatus.Ambiguous, candidates, error);
    }

    public static ChannelAddressResolutionResult Unsupported(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new ChannelAddressResolutionResult(ChannelAddressResolutionStatus.Unsupported, [], error);
    }

    public ResolvedChannelAddress RequireSingle()
    {
        return Status == ChannelAddressResolutionStatus.Resolved && Candidates.Count == 1
            ? Candidates[0]
            : throw new InvalidOperationException(Error ?? $"Address resolution did not produce a single result. Status: {Status}.");
    }
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

public interface IChannelAddressResolver
{
    ChannelDescriptorKey Key { get; }

    IReadOnlySet<ChannelAddressKind> AddressKinds { get; }

    ValueTask<ChannelAddressResolutionResult> ResolveAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IChannelRegistry
{
    IReadOnlyCollection<ChannelDescriptor> ListChannels();

    ChannelDescriptor GetChannel(ChannelDescriptorKey key);

    ValueTask<ChannelRuntimeSnapshot> GetSnapshotAsync(
        ChannelDescriptorKey key,
        CancellationToken cancellationToken = default);

    IChannelAddressResolver GetResolver(ChannelDescriptorKey key, ChannelAddressKind addressKind);

    ValueTask<ChannelAddressResolutionResult> ResolveAddressAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ChannelRegistry : IChannelRegistry
{
    private readonly IReadOnlyDictionary<ChannelDescriptorKey, ChannelDescriptor> _descriptors;
    private readonly IReadOnlyDictionary<ChannelDescriptorKey, IChannelRuntimeSnapshotProvider> _snapshotProviders;
    private readonly IReadOnlyDictionary<(ChannelDescriptorKey Key, ChannelAddressKind AddressKind), IChannelAddressResolver> _addressResolvers;

    public ChannelRegistry(
        IEnumerable<IChannelDescriptorProvider> descriptorProviders,
        IEnumerable<IChannelRuntimeSnapshotProvider> snapshotProviders,
        IEnumerable<IChannelAddressResolver>? addressResolvers = null)
    {
        ArgumentNullException.ThrowIfNull(descriptorProviders);
        ArgumentNullException.ThrowIfNull(snapshotProviders);

        _descriptors = BuildDescriptorLookup(descriptorProviders);
        _snapshotProviders = BuildSnapshotProviderLookup(snapshotProviders);
        _addressResolvers = BuildAddressResolverLookup(addressResolvers ?? []);
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

    public IChannelAddressResolver GetResolver(ChannelDescriptorKey key, ChannelAddressKind addressKind)
    {
        var descriptor = GetChannel(key);
        if (!descriptor.AddressKinds.Contains(addressKind))
            throw new InvalidOperationException($"Channel '{key}' does not support address kind '{addressKind}'.");

        if (_addressResolvers.TryGetValue((key, addressKind), out var resolver))
            return resolver;

        throw new InvalidOperationException(
            $"No channel address resolver is registered for key '{key}' and address kind '{addressKind}'.");
    }

    public async ValueTask<ChannelAddressResolutionResult> ResolveAddressAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolver = GetResolver(request.ChannelKey, request.AddressKind);
        return await resolver.ResolveAsync(request, cancellationToken);
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

    private static IReadOnlyDictionary<(ChannelDescriptorKey Key, ChannelAddressKind AddressKind), IChannelAddressResolver> BuildAddressResolverLookup(
        IEnumerable<IChannelAddressResolver> resolvers)
    {
        var addressResolvers = new Dictionary<(ChannelDescriptorKey Key, ChannelAddressKind AddressKind), IChannelAddressResolver>();

        foreach (var resolver in resolvers)
        {
            foreach (var addressKind in resolver.AddressKinds)
            {
                var key = (resolver.Key, addressKind);
                if (!addressResolvers.TryAdd(key, resolver))
                {
                    throw new InvalidOperationException(
                        $"Duplicate channel address resolver key '{resolver.Key}' for address kind '{addressKind}' registered.");
                }
            }
        }

        return addressResolvers;
    }
}

public sealed class StaticChannelDescriptorProvider(ChannelDescriptor descriptor) : IChannelDescriptorProvider
{
    public ChannelDescriptor GetDescriptor() => descriptor;
}

public sealed class DescriptorChannelRuntimeSnapshotProvider : IChannelRuntimeSnapshotProvider
{
    private readonly ChannelDescriptor _descriptor;
    private readonly Func<IEnumerable<IChannel>> _channelsAccessor;

    public DescriptorChannelRuntimeSnapshotProvider(
        ChannelDescriptor descriptor,
        IEnumerable<IChannel> channels)
        : this(descriptor, () => channels)
    {
    }

    public DescriptorChannelRuntimeSnapshotProvider(
        ChannelDescriptor descriptor,
        Func<IEnumerable<IChannel>> channelsAccessor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(channelsAccessor);

        _descriptor = descriptor;
        _channelsAccessor = channelsAccessor;
    }

    public ChannelDescriptorKey Key => _descriptor.Key;

    public async ValueTask<ChannelRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!_descriptor.IsEnabled)
        {
            return new ChannelRuntimeSnapshot(
                _descriptor.Key,
                IsEnabled: false,
                ChannelHealthStatus.Degraded,
                HealthDetail: $"{_descriptor.DisplayName} connector is disabled in configuration.",
                IsConnected: false,
                IsReady: false,
                Activity: BuildActivitySnapshot(_descriptor.ChannelType));
        }

        var channel = ResolveRuntimeChannel();
        if (channel is null)
        {
            return _descriptor.Kind == ChannelKind.LocalInteractiveClient
                ? new ChannelRuntimeSnapshot(
                    _descriptor.Key,
                    IsEnabled: true,
                    ChannelHealthStatus.Healthy,
                    IsReady: true,
                    Activity: BuildActivitySnapshot(_descriptor.ChannelType))
                : new ChannelRuntimeSnapshot(
                    _descriptor.Key,
                    IsEnabled: true,
                    ChannelHealthStatus.Disconnected,
                    HealthDetail: $"{_descriptor.DisplayName} connector is enabled but was not registered.",
                    IsConnected: false,
                    IsReady: false,
                    Activity: BuildActivitySnapshot(_descriptor.ChannelType));
        }

        var health = await channel.GetHealthAsync(cancellationToken);
        return new ChannelRuntimeSnapshot(
            _descriptor.Key,
            IsEnabled: true,
            health.Status,
            HealthDetail: health.Detail,
            IsConnected: health.Status != ChannelHealthStatus.Disconnected,
            IsReady: health.Status == ChannelHealthStatus.Healthy,
            Activity: BuildActivitySnapshot(_descriptor.ChannelType));
    }

    private IChannel? ResolveRuntimeChannel()
    {
        IChannel? match = null;
        foreach (var channel in _channelsAccessor())
        {
            if (channel.ChannelType != _descriptor.ChannelType)
                continue;

            if (match is not null)
                throw new InvalidOperationException($"Multiple runtime channels are registered for '{_descriptor.Key}'.");

            match = channel;
        }

        return match;
    }

    private static ChannelActivitySnapshot? BuildActivitySnapshot(ChannelType channelType)
    {
        var metrics = ChannelTelemetry.GetAllSnapshots()
            .FirstOrDefault(snapshot => snapshot.ChannelType == channelType);

        if (metrics is null)
            return null;

        return new ChannelActivitySnapshot(
            InputCount: metrics.EventsReceived,
            OutputCount: metrics.RepliesPosted);
    }
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

    public static IServiceCollection AddChannelDescriptorWithRuntimeSnapshot(
        this IServiceCollection services,
        ChannelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);

        services.AddChannelDescriptor(descriptor);
        services.AddSingleton<IChannelRuntimeSnapshotProvider>(sp =>
            new DescriptorChannelRuntimeSnapshotProvider(
                descriptor,
                () => sp.GetServices<IChannel>()));
        return services;
    }

    public static IServiceCollection AddChannelAddressResolver<TResolver>(this IServiceCollection services)
        where TResolver : class, IChannelAddressResolver
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TResolver>();
        services.AddSingleton<IChannelAddressResolver>(sp => sp.GetRequiredService<TResolver>());
        return services;
    }

    public static IServiceCollection AddTuiChannelDescriptor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddChannelDescriptorWithRuntimeSnapshot(new ChannelDescriptor(
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
