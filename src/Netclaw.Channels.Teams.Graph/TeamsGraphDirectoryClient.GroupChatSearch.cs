// -----------------------------------------------------------------------
// <copyright file="TeamsGraphDirectoryClient.GroupChatSearch.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
using Netclaw.Channels.Teams;

namespace Netclaw.Channels.Teams.Graph;

public sealed partial class TeamsGraphDirectoryClient
{
    internal const int GroupChatSearchRequestBudget = 10;
    private const int GroupChatSearchPageSize = 50;
    private const int GroupChatSearchMaximumResponseSize = 1_000;
    private const int GroupChatSearchMaximumMatches = 10_000;
    private const int GroupChatSearchMaximumCursors = 8;
    private readonly object _groupChatSearchStateLock = new();
    private readonly LinkedList<(string Handle, GroupChatSearchState State)> _groupChatSearchStates = new();

    public async ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>> SearchGroupChatsAsync(
        string query,
        int maximumResults,
        string? continuation = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeSearch(query, maximumResults, out var normalizedQuery, out var maximum, out var reason))
            return TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.InvalidRequest(reason);

        GroupChatSearchState state;
        if (continuation is null)
        {
            state = new GroupChatSearchState(normalizedQuery, maximum);
        }
        else if (!TryGetGroupChatSearchState(continuation, normalizedQuery, maximum, out state))
        {
            return TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.InvalidRequest("teams_directory_invalid_continuation");
        }

        var result = new List<TeamsDirectoryGroupChat>(maximum);
        for (var requests = 0; ;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (result.Count < maximum && state.PendingMatches.TryDequeue(out var chat))
                result.Add(chat);
            if (result.Count == maximum || state.IsComplete || requests == GroupChatSearchRequestBudget)
                break;

            requests++;
            var step = state.CurrentUser is not null || state.PendingUsers.Count > 0
                ? await ReadSearchChatPageAsync(state, cancellationToken).ConfigureAwait(false)
                : await ReadSearchUserPageAsync(state, cancellationToken).ConfigureAwait(false);
            if (!step.IsAvailable)
                return TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.Unavailable(step.ReasonCode!);
            if (state.MatchedIds.Count > GroupChatSearchMaximumMatches)
                return TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.Unavailable("teams_directory_search_limit_reached");
        }

        CacheRecords(
            TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroupChat>>.Available(result),
            "group-chat", static chat => chat.Id, DirectoryRecordTtl);
        var next = state.IsComplete ? null : StoreGroupChatSearchState(state);
        return TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.Available(
            new TeamsDirectoryGroupChatSearchPage(result, next, state.UsersExamined, state.UnavailableUsers));
    }

    private ValueTask<TeamsDirectoryOperationResult<bool>> ReadSearchUserPageAsync(
        GroupChatSearchState state, CancellationToken cancellationToken) =>
        ExecuteAsync(async token =>
        {
            var response = !state.UsersStarted
                ? await _graphClient.Users.GetAsync(request =>
                {
                    request.Options.Add(new RetryHandlerOption { MaxRetry = 0 });
                    request.QueryParameters.Select = ["id"];
                    request.QueryParameters.Top = GroupChatSearchPageSize;
                }, token).ConfigureAwait(false)
                : await _graphClient.Users.WithUrl(state.UsersNextLink!).GetAsync(
                    request => request.Options.Add(new RetryHandlerOption { MaxRetry = 0 }), token).ConfigureAwait(false);
            if (response?.Value is null || response.Value.Count > GroupChatSearchMaximumResponseSize)
                throw new InvalidDataException("The user page has no bounded value collection.");
            ValidateSearchNextLink(response.OdataNextLink, "/users");
            foreach (var user in response.Value)
            {
                if (!TryNormalizeIdentifier(user.Id, out var id))
                    throw new InvalidDataException("The user page has no canonical ID.");
                state.PendingUsers.Enqueue(id);
            }

            state.UsersStarted = true;
            state.UsersNextLink = NullIfEmpty(response.OdataNextLink);
            return true;
        }, cancellationToken, retry: false);

    private ValueTask<TeamsDirectoryOperationResult<bool>> ReadSearchChatPageAsync(
        GroupChatSearchState state, CancellationToken cancellationToken) =>
        ExecuteAsync(async token =>
        {
            state.CurrentUser ??= state.PendingUsers.Dequeue();
            ChatCollectionResponse? response;
            try
            {
                response = state.ChatsNextLink is null
                    ? await _graphClient.Users[state.CurrentUser].Chats.GetAsync(request =>
                    {
                        request.Options.Add(new RetryHandlerOption { MaxRetry = 0 });
                        request.QueryParameters.Top = GroupChatSearchPageSize;
                        request.QueryParameters.Expand = ["members"];
                    }, token).ConfigureAwait(false)
                    : await _graphClient.Users[state.CurrentUser].Chats.WithUrl(state.ChatsNextLink)
                        .GetAsync(request => request.Options.Add(new RetryHandlerOption { MaxRetry = 0 }), token).ConfigureAwait(false);
            }
            catch (ApiException exception) when (exception.ResponseStatusCode == 404)
            {
                state.UsersExamined++;
                state.UnavailableUsers++;
                state.CurrentUser = null;
                state.ChatsNextLink = null;
                return true;
            }

            if (response?.Value is null || response.Value.Count > GroupChatSearchMaximumResponseSize)
                throw new InvalidDataException("The chat page has no bounded value collection.");
            ValidateSearchNextLink(response.OdataNextLink, $"/users/{state.CurrentUser}/chats");
            foreach (var chat in response.Value)
            {
                if (chat.ChatType is null || string.IsNullOrWhiteSpace(chat.Id))
                    throw new InvalidDataException("The chat page has no chat type or ID.");
                if (chat.ChatType != ChatType.Group)
                    continue;
                if (!TeamsSessionIdentifierCodec.IsCanonicalGroupChatConversationId(chat.Id))
                    throw new InvalidDataException("The chat page has a noncanonical Group Chat ID.");
                if (chat.Topic?.Contains(state.Query, StringComparison.OrdinalIgnoreCase) == true
                    && state.MatchedIds.Add(chat.Id.Trim()))
                {
                    state.PendingMatches.Enqueue(ToGroupChat(chat));
                }
            }

            state.ChatsNextLink = NullIfEmpty(response.OdataNextLink);
            if (state.ChatsNextLink is null)
            {
                state.CurrentUser = null;
                state.UsersExamined++;
            }
            return true;
        }, cancellationToken, retry: false);

    private void ValidateSearchNextLink(string? nextLink, string collectionPath)
    {
        if (string.IsNullOrWhiteSpace(nextLink))
            return;
        if (!Uri.TryCreate(_graphClient.RequestAdapter.BaseUrl, UriKind.Absolute, out var graphBase)
            || !IsCurrentGraphUri(nextLink)
            || !Uri.TryCreate(nextLink, UriKind.Absolute, out var next)
            || !string.IsNullOrEmpty(next.UserInfo)
            || !string.IsNullOrEmpty(next.Fragment)
            || !string.Equals(Uri.UnescapeDataString(next.AbsolutePath),
                graphBase.AbsolutePath.TrimEnd('/') + collectionPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The page continuation does not belong to this Graph collection.");
        }
    }

    private string StoreGroupChatSearchState(GroupChatSearchState state)
    {
        var handle = Guid.NewGuid().ToString("N");
        state.ExpiresAt = _timeProvider.GetUtcNow() + GroupChatContinuationTtl;
        lock (_groupChatSearchStateLock)
        {
            // Metadata cache pressure must not discard a cursor immediately after publication.
            // Retain several snapshots so a cancelled UI generation can retry its previous page.
            while (_groupChatSearchStates.First is { } first
                   && (first.Value.State.ExpiresAt <= _timeProvider.GetUtcNow()
                       || _groupChatSearchStates.Count >= GroupChatSearchMaximumCursors))
            {
                _groupChatSearchStates.RemoveFirst();
            }
            _groupChatSearchStates.AddLast((handle, state));
        }
        return handle;
    }

    private bool TryGetGroupChatSearchState(string handle, string query, int maximum, out GroupChatSearchState state)
    {
        state = null!;
        if (!Guid.TryParseExact(handle, "N", out _))
            return false;
        lock (_groupChatSearchStateLock)
        {
            var cached = _groupChatSearchStates.FirstOrDefault(entry => entry.Handle == handle).State;
            if (cached is null || cached.ExpiresAt <= _timeProvider.GetUtcNow()
                || cached.Maximum != maximum || !string.Equals(cached.Query, query, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // A failed request or a cancelled UI generation must not consume a cursor.
            // This cache belongs to one immutable tenant/client credential instance.
            state = cached.Copy();
            return true;
        }
    }

    private void ClearGroupChatSearchStates()
    {
        lock (_groupChatSearchStateLock)
            _groupChatSearchStates.Clear();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class GroupChatSearchState(string query, int maximum)
    {
        public string Query { get; } = query;
        public int Maximum { get; } = maximum;
        public Queue<string> PendingUsers { get; private init; } = new();
        public Queue<TeamsDirectoryGroupChat> PendingMatches { get; private init; } = new();
        public HashSet<string> MatchedIds { get; private init; } = new(StringComparer.Ordinal);
        public bool UsersStarted { get; set; }
        public string? UsersNextLink { get; set; }
        public string? CurrentUser { get; set; }
        public string? ChatsNextLink { get; set; }
        public int UsersExamined { get; set; }
        public int UnavailableUsers { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public bool IsComplete => UsersStarted && UsersNextLink is null && PendingUsers.Count == 0
                                  && CurrentUser is null && PendingMatches.Count == 0;

        public GroupChatSearchState Copy() => new(Query, Maximum)
        {
            PendingUsers = new Queue<string>(PendingUsers),
            PendingMatches = new Queue<TeamsDirectoryGroupChat>(PendingMatches),
            MatchedIds = new HashSet<string>(MatchedIds, StringComparer.Ordinal),
            UsersStarted = UsersStarted,
            UsersNextLink = UsersNextLink,
            CurrentUser = CurrentUser,
            ChatsNextLink = ChatsNextLink,
            UsersExamined = UsersExamined,
            UnavailableUsers = UnavailableUsers,
            ExpiresAt = ExpiresAt
        };
    }
}
