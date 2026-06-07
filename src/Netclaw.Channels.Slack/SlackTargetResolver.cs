// -----------------------------------------------------------------------
// <copyright file="SlackTargetResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using SlackNet;
using SlackNet.WebApi;
using Netclaw.Channels;
using ChannelType = Netclaw.Actors.Channels.ChannelType;

namespace Netclaw.Channels.Slack;

public sealed record SlackTargetResolutionResult(
    bool Success,
    string? ErrorMessage,
    string? ChannelId,
    string? UserId);

/// <summary>
/// Resolves human-friendly Slack targets (e.g. #channel, @user, email) into canonical IDs.
/// </summary>
public interface ISlackTargetResolver
{
    Task<SlackTargetResolutionResult> ResolveAsync(string target, CancellationToken ct = default);
}

public sealed record SlackChannelPage(IReadOnlyList<Conversation> Channels, string? NextCursor);

public sealed record SlackUserPage(IReadOnlyList<User> Users, string? NextCursor);

public interface ISlackTargetLookupClient
{
    Task<SlackChannelPage> ListChannelsAsync(string? cursor, CancellationToken ct = default);
    Task<SlackUserPage> ListUsersAsync(string? cursor, CancellationToken ct = default);
}

public sealed class SlackApiTargetLookupClient(ISlackApiClient slackApi) : ISlackTargetLookupClient
{
    public async Task<SlackChannelPage> ListChannelsAsync(string? cursor, CancellationToken ct = default)
    {
        var page = await slackApi.Conversations.List(
            types: [ConversationType.PublicChannel, ConversationType.PrivateChannel],
            cursor: cursor,
            cancellationToken: ct);

        return new SlackChannelPage(page.Channels.ToList(), page.ResponseMetadata?.NextCursor);
    }

    public async Task<SlackUserPage> ListUsersAsync(string? cursor, CancellationToken ct = default)
    {
        var page = await slackApi.Users.List(cursor: cursor, cancellationToken: ct);
        return new SlackUserPage(page.Members.ToList(), page.ResponseMetadata?.NextCursor);
    }
}

public sealed class SlackTargetResolver(
    ISlackTargetLookupClient lookupClient,
    SlackChannelOptions options,
    Func<SlackChannelId?> defaultChannelIdAccessor) : ISlackTargetResolver, IChannelAddressResolver
{
    private static readonly IReadOnlySet<ChannelAddressKind> SupportedAddressKinds = new HashSet<ChannelAddressKind>
    {
        ChannelAddressKind.Destination
    };

    public ChannelDescriptorKey Key { get; } = ChannelDescriptorKey.FromChannelType(ChannelType.Slack);

    public IReadOnlySet<ChannelAddressKind> AddressKinds => SupportedAddressKinds;

    public async Task<SlackTargetResolutionResult> ResolveAsync(string target, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target))
            return new SlackTargetResolutionResult(false, "Target is required.", null, null);

        var raw = target.Trim();

        if (raw.StartsWith("C", StringComparison.Ordinal) || raw.StartsWith("G", StringComparison.Ordinal))
        {
            var channelId = new SlackChannelId(raw);
            return SlackAclPolicy.IsAllowedChannel(channelId, options, defaultChannelIdAccessor())
                ? new SlackTargetResolutionResult(true, null, raw, null)
                : new SlackTargetResolutionResult(false, $"Slack channel '{raw}' is not in the allowed channels list.", null, null);
        }

        if (raw.StartsWith("U", StringComparison.Ordinal))
        {
            var userId = new SlackUserId(raw);
            return SlackAclPolicy.IsAllowedUser(userId, options)
                ? new SlackTargetResolutionResult(true, null, null, raw)
                : new SlackTargetResolutionResult(false, $"Slack user '{raw}' is not in the allowed users list.", null, null);
        }

        if (raw.StartsWith("#", StringComparison.Ordinal))
        {
            var channelName = raw[1..].Trim();
            if (string.IsNullOrWhiteSpace(channelName))
                return new SlackTargetResolutionResult(false, "Channel name is empty.", null, null);

            var channelId = await ResolveChannelByNameAsync(channelName, ct);
            return channelId is not null
                ? new SlackTargetResolutionResult(true, null, channelId, null)
                : new SlackTargetResolutionResult(false, $"Could not resolve Slack channel '{raw}'.", null, null);
        }

        if (raw.StartsWith("@", StringComparison.Ordinal))
        {
            var userQuery = raw[1..].Trim();
            if (string.IsNullOrWhiteSpace(userQuery))
                return new SlackTargetResolutionResult(false, "User name is empty.", null, null);

            var userId = await ResolveUserAsync(userQuery, ct);
            return userId is not null
                ? new SlackTargetResolutionResult(true, null, null, userId)
                : new SlackTargetResolutionResult(false, $"Could not resolve Slack user '{raw}'.", null, null);
        }

        var fallbackChannelId = await ResolveChannelByNameAsync(raw, ct);
        if (fallbackChannelId is not null)
            return new SlackTargetResolutionResult(true, null, fallbackChannelId, null);

        var fallbackUserId = await ResolveUserAsync(raw, ct);
        if (fallbackUserId is not null)
            return new SlackTargetResolutionResult(true, null, null, fallbackUserId);

        return new SlackTargetResolutionResult(
            false,
            $"Could not resolve Slack target '{target}'. Use #channel, @user, or a Slack ID (C..., G..., U...).",
            null,
            null);
    }

    public async ValueTask<ChannelAddressResolutionResult> ResolveAsync(
        ChannelAddressResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.ChannelKey.Equals(Key))
            return ChannelAddressResolutionResult.Unsupported($"Slack resolver cannot resolve channel key '{request.ChannelKey}'.");

        if (request.AddressKind != ChannelAddressKind.Destination)
            return ChannelAddressResolutionResult.Unsupported($"Slack destination resolver does not support address kind '{request.AddressKind}'.");

        var raw = request.Query.Trim();
        if (raw.StartsWith('#'))
            raw = raw[1..].Trim();

        if (IsSlackChannelId(raw))
        {
            var channelId = new SlackChannelId(raw);
            return SlackAclPolicy.IsAllowedChannel(channelId, options, defaultChannelIdAccessor())
                ? ChannelAddressResolutionResult.Resolved(new ResolvedChannelAddress(Key, request.AddressKind, raw, raw))
                : ChannelAddressResolutionResult.NotFound($"Slack channel '{raw}' is not in the allowed channels list.");
        }

        var matches = await FindChannelMatchesAsync(raw, cancellationToken);
        return ToResolutionResult(request.AddressKind, matches, raw);
    }

    private async Task<string?> ResolveChannelByNameAsync(string channelName, CancellationToken ct)
    {
        var cursor = default(string);
        do
        {
            var page = await lookupClient.ListChannelsAsync(cursor, ct);

            var match = page.Channels.FirstOrDefault(c =>
                IsAllowedChannel(c)
                && (string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.NameNormalized, channelName, StringComparison.OrdinalIgnoreCase)));

            if (match is not null)
                return match.Id;

            cursor = page.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return null;
    }

    private async Task<List<ResolvedChannelAddress>> FindChannelMatchesAsync(string query, CancellationToken ct)
    {
        var matches = new List<ResolvedChannelAddress>();
        var cursor = default(string);

        do
        {
            var page = await lookupClient.ListChannelsAsync(cursor, ct);
            foreach (var channel in page.Channels)
            {
                if (!IsAllowedChannel(channel) || !MatchesChannelQuery(channel, query))
                    continue;

                var displayName = string.IsNullOrWhiteSpace(channel.Name)
                    ? channel.Id
                    : $"#{channel.Name}";
                matches.Add(new ResolvedChannelAddress(Key, ChannelAddressKind.Destination, channel.Id, displayName));
            }

            cursor = page.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return matches;
    }

    private ChannelAddressResolutionResult ToResolutionResult(
        ChannelAddressKind addressKind,
        IReadOnlyList<ResolvedChannelAddress> matches,
        string query)
    {
        if (matches.Count == 0)
            return ChannelAddressResolutionResult.NotFound($"No Slack {addressKind} matched '{query}'.");

        if (matches.Count == 1)
            return ChannelAddressResolutionResult.Resolved(matches[0]);

        return ChannelAddressResolutionResult.Ambiguous(
            matches,
            $"Slack {addressKind} query '{query}' matched {matches.Count} destinations.");
    }

    private bool IsAllowedChannel(Conversation channel)
    {
        if (string.IsNullOrWhiteSpace(channel.Id))
            return false;

        return SlackAclPolicy.IsAllowedChannel(new SlackChannelId(channel.Id), options, defaultChannelIdAccessor());
    }

    private static bool MatchesChannelQuery(Conversation channel, string query)
    {
        var name = channel.Name ?? string.Empty;
        var normalizedName = channel.NameNormalized ?? string.Empty;

        return name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSlackChannelId(string value)
    {
        return value.StartsWith("C", StringComparison.Ordinal) || value.StartsWith("G", StringComparison.Ordinal);
    }

    private async Task<string?> ResolveUserAsync(string query, CancellationToken ct)
    {
        var cursor = default(string);
        var exactMatches = new List<User>();

        do
        {
            var response = await lookupClient.ListUsersAsync(cursor, ct);
            foreach (var user in response.Users)
            {
                if (user.IsBot || user.Deleted)
                    continue;

                if (!SlackAclPolicy.IsAllowedUser(new SlackUserId(user.Id), options))
                    continue;

                var displayName = user.Profile?.DisplayName ?? string.Empty;
                var realName = user.RealName ?? string.Empty;
                var username = user.Name ?? string.Empty;
                var email = user.Profile?.Email ?? string.Empty;

                if (string.Equals(email, query, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(displayName, query, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(realName, query, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(username, query, StringComparison.OrdinalIgnoreCase))
                {
                    exactMatches.Add(user);
                }
            }

            cursor = response.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        if (exactMatches.Count == 1)
            return exactMatches[0].Id;

        return null;
    }
}
