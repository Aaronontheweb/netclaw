// -----------------------------------------------------------------------
// <copyright file="MattermostDestinationAddressResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;

namespace Netclaw.Channels.Mattermost;

public sealed class MattermostDestinationAddressResolver(
    MattermostChannelOptions options,
    Func<MattermostChannelId?> defaultChannelIdAccessor) : IChannelAddressResolver
{
    private static readonly IReadOnlySet<ChannelAddressKind> SupportedAddressKinds = new HashSet<ChannelAddressKind>
    {
        ChannelAddressKind.Destination
    };

    public ChannelDescriptorKey Key { get; } = ChannelDescriptorKey.FromChannelType(ChannelType.Mattermost);

    public IReadOnlySet<ChannelAddressKind> AddressKinds => SupportedAddressKinds;

    public ValueTask<ChannelAddressResolutionResult> ResolveAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.ChannelKey.Equals(Key))
            return ValueTask.FromResult(ChannelAddressResolutionResult.Unsupported($"Mattermost destination resolver cannot resolve channel key '{request.ChannelKey}'."));

        if (request.AddressKind != ChannelAddressKind.Destination)
            return ValueTask.FromResult(ChannelAddressResolutionResult.Unsupported($"Mattermost destination resolver does not support address kind '{request.AddressKind}'."));

        var channelId = NormalizeDestinationQuery(request.Query);
        if (!MattermostIdentifierFormat.IsMattermostId(channelId))
        {
            return ValueTask.FromResult(ChannelAddressResolutionResult.NotFound(
                $"Mattermost destination lookup requires an exact channel ID."));
        }

        var target = new MattermostChannelId(channelId);
        return ValueTask.FromResult(MattermostAclPolicy.IsAllowedChannel(target, options, defaultChannelIdAccessor())
            ? ChannelAddressResolutionResult.Resolved(new ResolvedChannelAddress(Key, request.AddressKind, channelId, channelId))
            : ChannelAddressResolutionResult.NotFound($"Mattermost channel '{channelId}' is not in the allowed channels list."));
    }

    private static string NormalizeDestinationQuery(string query)
    {
        var normalized = query.Trim();
        return normalized.StartsWith("channel:", StringComparison.OrdinalIgnoreCase)
            ? normalized[8..].Trim()
            : normalized;
    }
}
