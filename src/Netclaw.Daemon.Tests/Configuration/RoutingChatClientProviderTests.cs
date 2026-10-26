// -----------------------------------------------------------------------
// <copyright file="RoutingChatClientProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class RoutingChatClientProviderTests
{
    [Fact]
    public void GetClient_caches_per_role_and_shares_Main_and_Fallback()
    {
        var provider = new RoutingChatClientProvider(
            new StubRouter(), NullNotificationSink.Instance, NullLoggerFactory.Instance);

        var main = provider.GetClient(ModelRole.Main);

        Assert.Same(main, provider.GetClient(ModelRole.Main));         // cached per role
        Assert.Same(main, provider.GetClient(ModelRole.Fallback));     // Fallback shares the Main routing client
        Assert.NotSame(main, provider.GetClient(ModelRole.Compaction));// Compaction is its own routing client
        Assert.Same(provider.GetClient(ModelRole.Compaction), provider.GetClient(ModelRole.Compaction));
    }

    private sealed class StubRouter : IChatClientRouter
    {
        private readonly IReadOnlyList<IChatClient> _candidates = [new FakeChatClient()];
        public IReadOnlyList<IChatClient> Route(ChatRoutingContext context) => _candidates;
    }
}

public sealed class RoleBasedFailoverRouterTests
{
    [Fact]
    public void Main_has_two_candidates_when_fallback_configured()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "p", ModelId = "main" },
            Fallback = new ModelReference { Provider = "p", ModelId = "fb" }
        };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), models);

        var main = router.Route(new ChatRoutingContext { Role = ModelRole.Main });

        Assert.Equal(2, main.Count);
        Assert.Same(main, router.Route(new ChatRoutingContext { Role = ModelRole.Fallback }));
        // No distinct compaction model → compaction reuses the main candidate list.
        Assert.Same(main, router.Route(new ChatRoutingContext { Role = ModelRole.Compaction }));
    }

    [Fact]
    public void Main_has_one_candidate_when_no_fallback()
    {
        var models = new ModelSelection { Main = new ModelReference { Provider = "p", ModelId = "main" } };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), models);

        Assert.Single(router.Route(new ChatRoutingContext { Role = ModelRole.Main }));
    }

    [Fact]
    public void Compaction_is_a_distinct_single_pipeline_when_configured()
    {
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "p", ModelId = "main" },
            Compaction = new ModelReference { Provider = "p", ModelId = "comp" }
        };
        var router = new RoleBasedFailoverRouter(_ => new FakeChatClient(), models);

        var main = router.Route(new ChatRoutingContext { Role = ModelRole.Main });
        var compaction = router.Route(new ChatRoutingContext { Role = ModelRole.Compaction });

        Assert.Single(compaction);
        Assert.NotSame(main, compaction);
    }

    // Two references name the same model when Provider and ModelId both match; ModelReference
    // has no separate definition ID in this version, and the other fields are tuning data.
    [Theory]
    [InlineData("fallback-set-compaction-distinct", "fb", "comp", "main,fb", "comp,fb")]
    [InlineData("fallback-set-compaction-equals-main", "fb", "main", "main,fb", "main,fb")]
    [InlineData("no-fallback-compaction-distinct", null, "comp", "main", "comp")]
    [InlineData("no-compaction", "fb", null, "main,fb", "main,fb")]
    [InlineData("no-compaction-no-fallback", null, null, "main", "main")]
    [InlineData("compaction-equals-fallback", "fb", "fb", "main,fb", "fb")]
    public void Candidate_lists_per_role(
        string row, string? fallbackId, string? compactionId, string expectedMain, string expectedCompaction)
    {
        var models = new ModelSelection
        {
            Main = Model("main"),
            Fallback = fallbackId is null ? null : Model(fallbackId),
            Compaction = compactionId is null ? null : Model(compactionId)
        };
        var modelIdByClient = new Dictionary<IChatClient, string>(ReferenceEqualityComparer.Instance);
        var router = new RoleBasedFailoverRouter(r =>
        {
            var client = new FakeChatClient();
            modelIdByClient[client] = r.ModelId;
            return client;
        }, models);

        string Ids(ModelRole role) => string.Join(",",
            router.Route(new ChatRoutingContext { Role = role }).Select(c => modelIdByClient[c]));

        Assert.True(expectedMain == Ids(ModelRole.Main), $"{row}: Main was [{Ids(ModelRole.Main)}]");
        Assert.True(expectedMain == Ids(ModelRole.Fallback), $"{row}: Fallback was [{Ids(ModelRole.Fallback)}]");
        Assert.True(expectedCompaction == Ids(ModelRole.Compaction), $"{row}: Compaction was [{Ids(ModelRole.Compaction)}]");
    }

    [Fact]
    public async Task Compaction_role_call_fails_over_to_fallback_when_primary_refuses_connection()
    {
        var models = new ModelSelection
        {
            Main = Model("main"),
            Fallback = Model("fb"),
            Compaction = Model("comp")
        };
        var router = new RoleBasedFailoverRouter(r => r.ModelId == "fb"
            ? new FakeChatClient((_, _, _) =>
                Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "fallback")])))
            : new FakeChatClient((_, _, _) => throw new HttpRequestException("connection refused")), models);
        var provider = new RoutingChatClientProvider(
            router, NullNotificationSink.Instance, NullLoggerFactory.Instance);

        var response = await provider.GetClient(ModelRole.Compaction)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fallback", response.Messages[0].Text);
    }

    private static ModelReference Model(string modelId) => new() { Provider = "p", ModelId = modelId };
}
