// -----------------------------------------------------------------------
// <copyright file="LlmSessionPrefillKeepaliveTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Guards the session-path half of the two-phase watchdog: content-free keepalives
/// must refresh the generous prefill budget and NOT promote to the tighter
/// inter-delta budget. Uses a distinct config where PrefillTimeout (6s) is much
/// larger than FirstTokenTimeout (1s) so the distinction is observable — the shared
/// <see cref="LlmSessionStreamingTimeoutTests"/> deliberately sets them equal.
/// </summary>
public sealed class LlmSessionPrefillKeepaliveTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private readonly KeepaliveThenTextChatClient _chatClient = new();

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "prefill-keepalive-test-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            // Prefill far exceeds the keepalive phase; FirstTokenTimeout is shorter
            // than the keepalive spacing, so promoting on a keepalive (the old bug)
            // would time out the turn mid-prefill.
            PrefillTimeout = TimeSpan.FromSeconds(6),
            FirstTokenTimeout = TimeSpan.FromSeconds(1),
            ToolExecutionTimeout = TimeSpan.FromSeconds(10),
            SidecarLlmTimeout = TimeSpan.FromSeconds(10),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
    }

    [Fact]
    public async Task Keepalives_during_prefill_do_not_promote_to_the_tighter_budget()
    {
        var sessionId = new SessionId("prefill-keepalive/holds-prefill");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("prefill-keepalive-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.TextOnly
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "hello"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Keepalives space ~1.3s apart (> FirstTokenTimeout 1s) for ~4s, then a real
        // token. If the session promoted on the first keepalive it would be on the 1s
        // inter-delta budget and time out before the next keepalive. Holding the 6s
        // prefill budget across keepalives lets the turn complete successfully.
        var text = await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(12), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("success", text.Text, StringComparison.OrdinalIgnoreCase);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class KeepaliveThenTextChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(new NotSupportedException("Streaming path only."));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Content-free keepalives (e.g. prompt_progress) before the first token.
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1300), cancellationToken);
                yield return new ChatResponseUpdate { Role = AiChatRole.Assistant };
            }

            // First real tokens — two chunks so the session's buffered-first-delta
            // flushes a streamed TextOutput.
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            yield return new ChatResponseUpdate { Role = AiChatRole.Assistant, Contents = [new TextContent("success ")] };
            await Task.Yield();
            yield return new ChatResponseUpdate { Contents = [new TextContent("response")] };
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
