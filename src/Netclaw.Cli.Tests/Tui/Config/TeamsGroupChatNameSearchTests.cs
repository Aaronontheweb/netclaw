// -----------------------------------------------------------------------
// <copyright file="TeamsGroupChatNameSearchTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Teams.Graph;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class TeamsGroupChatNameSearchTests
{
    [Fact]
    public async Task Name_search_matches_chat_topic_without_a_matching_user_name()
    {
        using var fixture = new SearchFixture((_, index, _) => index switch
        {
            0 => Json(Page(new[] { new { id = "user-a", displayName = "Alice" } })),
            1 => Json(Page(new[]
            {
                Chat("one", "The BostonTech project"),
                Chat("two", "BostonTech meeting", "meeting"),
                Chat("three", "BostonTech personal", "oneOnOne"),
                Chat("four", "Another project")
            })),
            _ => throw new InvalidOperationException("Unexpected request.")
        });

        var result = await fixture.Directory.SearchGroupChatsAsync("bostontech", 25, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Equal("19:one@thread.v2", Assert.Single(result.Value!.Chats).Id);
        Assert.Null(result.Value.Continuation);
        Assert.Equal(1, result.Value.UsersExamined);
        Assert.Equal("/v1.0/users", fixture.Handler.Requests[0].AbsolutePath);
        Assert.All(fixture.Handler.Requests, uri =>
        {
            var query = Uri.UnescapeDataString(uri.Query);
            Assert.DoesNotContain("bostontech", query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("$filter", query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("$search", query, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal("/v1.0/users/user-a/chats", fixture.Handler.Requests[1].AbsolutePath);
    }

    [Fact]
    public async Task Name_search_follows_both_page_types_and_preserves_overflow_and_distinct_same_topic_chats()
    {
        using var fixture = new SearchFixture((_, index, _) => index switch
        {
            0 => Json(Page(new[] { new { id = "user-a" } }, "https://graph.test/v1.0/users?$skiptoken=users-two")),
            1 => Json(Page(new[] { Chat("one", "BostonTech"), Chat("two", "BostonTech") },
                "https://graph.test/v1.0/users/user-a/chats?$skiptoken=chats-two")),
            2 => Json(Page(new[] { Chat("one", "BostonTech"), Chat("three", "BostonTech") })),
            3 => Json(Page(new[] { new { id = "user-b" } })),
            4 => Json(Page(new[] { Chat("three", "BostonTech"), Chat("four", "BostonTech") })),
            _ => throw new InvalidOperationException("Unexpected request.")
        });
        var ids = new List<string>();
        string? continuation = null;
        TeamsDirectoryGroupChatSearchPage? page = null;
        for (var index = 0; index < 4; index++)
        {
            var result = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 1, continuation, TestContext.Current.CancellationToken);
            Assert.True(result.IsAvailable);
            page = result.Value!;
            ids.Add(Assert.Single(page.Chats).Id);
            continuation = page.Continuation;
            if (index < 3)
                Assert.NotNull(continuation);
        }

        Assert.Equal(new[] { "19:one@thread.v2", "19:two@thread.v2", "19:three@thread.v2", "19:four@thread.v2" }, ids);
        Assert.Null(continuation);
        Assert.Equal(2, page!.UsersExamined);
        Assert.Equal(5, fixture.Handler.Requests.Count);
        Assert.Contains("chats-two", fixture.Handler.Requests[2].Query, StringComparison.Ordinal);
        Assert.Contains("users-two", fixture.Handler.Requests[3].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Name_search_stops_at_request_budget_and_reports_partial_scan_without_false_no_match()
    {
        using var fixture = new SearchFixture((uri, index, _) => index == 0
            ? Json(Page(Enumerable.Range(1, 11).Select(number => new { id = $"user-{number}" })))
            : Json(Page(uri.AbsolutePath.EndsWith("user-11/chats", StringComparison.Ordinal)
                ? new[] { Chat("last", "BostonTech") } : [])));

        var first = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 25, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first.IsAvailable);
        Assert.Empty(first.Value!.Chats);
        Assert.Equal(9, first.Value.UsersExamined);
        Assert.NotNull(first.Value.Continuation);
        Assert.Equal(10, fixture.Handler.Requests.Count);

        var second = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 25, first.Value.Continuation, TestContext.Current.CancellationToken);

        Assert.True(second.IsAvailable);
        Assert.Equal("19:last@thread.v2", Assert.Single(second.Value!.Chats).Id);
        Assert.Equal(11, second.Value.UsersExamined);
        Assert.Null(second.Value.Continuation);
    }

    [Fact]
    public async Task Name_search_cursor_rejects_changed_query_limit_tenant_and_expiry_before_network_access()
    {
        var clock = new FakeTimeProvider();
        using var fixture = new SearchFixture((_, index, _) => index == 0
            ? Json(Page(new[] { new { id = "user-a" } }))
            : Json(Page(new[] { Chat("one", "BostonTech"), Chat("two", "BostonTech") })), clock);
        var first = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 1, cancellationToken: TestContext.Current.CancellationToken);
        var cursor = first.Value!.Continuation;
        Assert.NotNull(cursor);
        Assert.True(Guid.TryParseExact(cursor, "N", out _));
        using var otherTenant = new TeamsGraphDirectoryClient(fixture.Graph, "tenant-b", fixture.Cache, clock);

        var query = await fixture.Directory.SearchGroupChatsAsync("Other", 1, cursor, TestContext.Current.CancellationToken);
        var limit = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 2, cursor, TestContext.Current.CancellationToken);
        var tenant = await otherTenant.SearchGroupChatsAsync("BostonTech", 1, cursor, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(6));
        var expired = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 1, cursor, TestContext.Current.CancellationToken);

        Assert.All(new[] { query, limit, tenant, expired }, result =>
        {
            Assert.Equal(TeamsDirectoryOperationStatus.InvalidRequest, result.Status);
            Assert.Equal("teams_directory_invalid_continuation", result.ReasonCode);
        });
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task Metadata_cache_pressure_cannot_discard_a_new_search_cursor()
    {
        using var fixture = new SearchFixture((_, index, _) => index == 0
            ? Json(Page(new[] { new { id = "user-a" } }))
            : Json(Page(new[] { Chat("one", "BostonTech"), Chat("two", "BostonTech") })));
        fixture.Cache.Set("unrelated-directory-records", true, new MemoryCacheEntryOptions { Size = 1_024 });

        var first = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(first.IsAvailable);
        Assert.NotNull(first.Value!.Continuation);
        var next = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 1, first.Value.Continuation, TestContext.Current.CancellationToken);

        Assert.True(next.IsAvailable);
        Assert.Equal("19:two@thread.v2", Assert.Single(next.Value!.Chats).Id);
        Assert.Null(next.Value.Continuation);
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "teams_directory_authentication_failed")]
    [InlineData(HttpStatusCode.Forbidden, "teams_directory_permission_denied")]
    [InlineData(HttpStatusCode.TooManyRequests, "teams_directory_network_unavailable")]
    public async Task Name_search_reports_graph_errors_and_does_not_retry_outside_its_budget(HttpStatusCode status, string reason)
    {
        using var fixture = new SearchFixture((_, index, _) => index == 0
            ? Json(Page(new[] { new { id = "user-a" } }))
            : new HttpResponseMessage(status));

        var result = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 25, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Null(result.Value);
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":[{\"id\":\"not-a-canonical-chat\",\"chatType\":\"group\"}]}")]
    [InlineData("{\"value\":[{\"id\":\"19:one@thread.v2\"}]}")]
    [InlineData("{\"value\":[],\"@odata.nextLink\":\"https://other.test/v1.0/users/user-a/chats\"}")]
    [InlineData("{\"value\":[],\"@odata.nextLink\":\"https://graph.test/v1.0/users/user-b/chats\"}")]
    public async Task Name_search_reports_malformed_or_untrusted_chat_pages(string response)
    {
        using var fixture = new SearchFixture((_, index, _) => Json(index == 0
            ? Page(new[] { new { id = "user-a" } }) : response));

        var result = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 25, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.Equal("teams_directory_malformed_response", result.ReasonCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Name_search_counts_unavailable_users_instead_of_claiming_full_coverage()
    {
        using var fixture = new SearchFixture((_, index, _) => index switch
        {
            0 => Json(Page(new[] { new { id = "user-a" }, new { id = "user-b" } })),
            1 => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => Json(Page(new[] { Chat("one", "BostonTech") }))
        });

        var result = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 25, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Single(result.Value!.Chats);
        Assert.Equal(2, result.Value.UsersExamined);
        Assert.Equal(1, result.Value.UnavailableUsers);
        Assert.Null(result.Value.Continuation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_batch_does_not_consume_the_previous_cursor(bool cancel)
    {
        using var cancelledRequest = new CancellationTokenSource();
        using var fixture = new SearchFixture((_, index, token) => index switch
        {
            0 => Json(Page(new[] { new { id = "user-a" }, new { id = "user-b" }, new { id = "user-c" } })),
            1 => Json(Page(new[] { Chat("one", "BostonTech"), Chat("two", "BostonTech") })),
            2 or 4 => Json(Page(new[] { Chat("three", "BostonTech") })),
            3 => FailOrCancel(cancel, cancelledRequest, token),
            5 => Json(Page(new[] { Chat("four", "BostonTech") })),
            _ => throw new InvalidOperationException("Unexpected request.")
        });
        var first = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 2, cancellationToken: TestContext.Current.CancellationToken);
        var cursor = first.Value!.Continuation;
        Assert.NotNull(cursor);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await fixture.Directory.SearchGroupChatsAsync("BostonTech", 2, cursor, cancelledRequest.Token));
        }
        else
        {
            var failed = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 2, cursor, TestContext.Current.CancellationToken);
            Assert.False(failed.IsAvailable);
        }

        var retry = await fixture.Directory.SearchGroupChatsAsync("BostonTech", 2, cursor, TestContext.Current.CancellationToken);

        Assert.True(retry.IsAvailable);
        Assert.Equal(new[] { "19:three@thread.v2", "19:four@thread.v2" }, retry.Value!.Chats.Select(chat => chat.Id));
        Assert.Equal(3, retry.Value.UsersExamined);
        Assert.Null(retry.Value.Continuation);
        Assert.Equal(fixture.Handler.Requests[2].AbsolutePath, fixture.Handler.Requests[4].AbsolutePath);
    }

    private static HttpResponseMessage FailOrCancel(bool cancel, CancellationTokenSource source, CancellationToken token)
    {
        if (cancel)
        {
            source.Cancel();
            token.ThrowIfCancellationRequested();
        }
        return new HttpResponseMessage(HttpStatusCode.Forbidden);
    }

    private static object Chat(string id, string topic, string chatType = "group") => new
    {
        id = $"19:{id}@thread.v2",
        topic,
        chatType,
        members = new[] { new { displayName = "Alice" } }
    };

    private static string Page<T>(IEnumerable<T> values, string? next = null) => JsonSerializer.Serialize(
        new Dictionary<string, object?> { ["value"] = values, ["@odata.nextLink"] = next });

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class SearchHandler(Func<Uri, int, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var index = Requests.Count;
            Requests.Add(uri);
            return Task.FromResult(response(uri, index, cancellationToken));
        }
    }

    private sealed class SearchFixture : IDisposable
    {
        private readonly HttpClient _httpClient;
        public SearchHandler Handler { get; }
        public GraphServiceClient Graph { get; }
        public MemoryCache Cache { get; } = new(new MemoryCacheOptions { SizeLimit = 1_024 });
        public TeamsGraphDirectoryClient Directory { get; }

        public SearchFixture(Func<Uri, int, CancellationToken, HttpResponseMessage> response, TimeProvider? time = null)
        {
            Handler = new SearchHandler(response);
            _httpClient = new HttpClient(new RetryHandler { InnerHandler = Handler });
            Graph = new GraphServiceClient(_httpClient, new AnonymousAuthenticationProvider(), "https://graph.test/v1.0");
            Directory = new TeamsGraphDirectoryClient(Graph, "tenant-a", Cache, time);
        }

        public void Dispose()
        {
            Directory.Dispose();
            Graph.Dispose();
            _httpClient.Dispose();
            Handler.Dispose();
            Cache.Dispose();
        }
    }
}
