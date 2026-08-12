// -----------------------------------------------------------------------
// <copyright file="StreamStallGuardChatClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// All tests drive a <see cref="FakeTimeProvider"/> — no real <c>Task.Delay</c> or
/// <c>Thread.Sleep</c> waits. A "stall" is simulated with
/// <c>Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)</c>, which never
/// completes on its own and resolves only when the guard cancels it — exactly the
/// dead/half-open-connection shape under test (no more tokens, no error, no close).
/// </summary>
public sealed class StreamStallGuardChatClientTests
{
    private static readonly RetryPolicy Policy = new() { StreamInactivityTimeout = TimeSpan.FromSeconds(45) };

    [Fact]
    public async Task StallAfterFirstDelta_CancelsWithinInactivityWindow_AndThrowsRetryableTimeout()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var leaf = new FakeChatClient(streamHandler: (_, _, ct) => YieldTwoThenStallForever(ct));
        var client = new StreamStallGuardChatClient(leaf, Policy, NullLogger.Instance, time);

        await using var enumerator = client
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync()); // chunk 1
        Assert.True(await enumerator.MoveNextAsync()); // chunk 2

        // Provider goes silent after the second delta: the guard's inactivity clock,
        // armed after the first delta, is now the only thing that can unblock this.
        var pending = enumerator.MoveNextAsync().AsTask();
        time.Advance(Policy.StreamInactivityTimeout);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => pending);

        // The whole point of a fast, real-time detector is that the existing transient-
        // failure retry policy already knows what to do with the exception it throws —
        // no new retry mechanism needed.
        Assert.True(Policy.ShouldRetry(ex, attempt: 0));
    }

    [Fact]
    public async Task SlowButProgressingStream_IsNotFalselyAborted()
    {
        // Every gap is 5s under the 45s window — a legitimately paced stream (e.g. a
        // heavily loaded self-hosted backend) must be allowed to finish.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gap = TimeSpan.FromSeconds(5);
        var leaf = new FakeChatClient(streamHandler: (_, _, ct) => SlowButProgressing(time, gap, count: 6, ct));
        var client = new StreamStallGuardChatClient(leaf, Policy, NullLogger.Instance, time);

        await using var enumerator = client
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        for (var i = 0; i < 6; i++)
        {
            var pending = enumerator.MoveNextAsync().AsTask();
            time.Advance(gap);
            Assert.True(await pending);
        }

        Assert.False(await enumerator.MoveNextAsync()); // clean completion, never aborted
    }

    [Fact]
    public async Task StallBeforeFirstDelta_IsNotGovernedByTheGuard()
    {
        // Time to first byte is left to the existing, more generous per-call watchdog —
        // a self-hosted backend can be legitimately silent for minutes during cold
        // prefill with no keepalive to reset a tighter timer. Advancing well past the
        // inactivity window before any delta arrives must not trip this guard.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var prefillDelay = Policy.StreamInactivityTimeout * 4;
        var leaf = new FakeChatClient(streamHandler: (_, _, ct) => DelayThenYieldOnce(time, prefillDelay, ct));
        var client = new StreamStallGuardChatClient(leaf, Policy, NullLogger.Instance, time);

        await using var enumerator = client
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var pending = enumerator.MoveNextAsync().AsTask();

        // Cross well past what would be the inactivity window if it were armed.
        time.Advance(Policy.StreamInactivityTimeout * 2);
        Assert.False(pending.IsCompleted);

        // Finish the (legitimately long) prefill — the first delta still arrives cleanly.
        time.Advance(prefillDelay - Policy.StreamInactivityTimeout * 2);
        Assert.True(await pending);
    }

    [Fact]
    public async Task KeepaliveOnlyUpdates_DoNotArmTheGuard_BeforeAnySubstantiveContent()
    {
        // Content-free keepalives (e.g. llama-server's prompt_progress heartbeat)
        // must not arm the tight inactivity timer — only a substantive update
        // (ChatStreamUpdateClassifier.IsSubstantiveUpdate) may promote it. Two keepalives
        // arrive, then the provider takes a long-but-legitimate time (a cold prefill,
        // not a stall) to produce the first substantive delta: crossing what would be
        // the inactivity window must not abort it, because no substantive content has
        // arrived yet to arm the guard.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var prefillDelay = Policy.StreamInactivityTimeout * 4;
        var leaf = new FakeChatClient(streamHandler: (_, _, ct) => KeepaliveTwiceThenDelayedSubstantive(time, prefillDelay, ct));
        var client = new StreamStallGuardChatClient(leaf, Policy, NullLogger.Instance, time);

        await using var enumerator = client
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync()); // keepalive 1 — non-substantive
        Assert.True(await enumerator.MoveNextAsync()); // keepalive 2 — still non-substantive

        var pending = enumerator.MoveNextAsync().AsTask();

        // Cross well past what would be the inactivity window if a keepalive had
        // (wrongly) armed the guard — the leaf is still legitimately working (a long
        // cold prefill), not stalled.
        time.Advance(Policy.StreamInactivityTimeout * 2);
        Assert.False(pending.IsCompleted);

        // Finish the (legitimately long) prefill — the first substantive update still
        // arrives cleanly, proving a keepalive never armed the guard.
        time.Advance(prefillDelay - Policy.StreamInactivityTimeout * 2);
        Assert.True(await pending);
    }

    [Fact]
    public async Task SlowConsumer_HoldingAnUpdate_DoesNotCountAgainstTheInactivityWindow()
    {
        // The inactivity timer must measure provider silence only, not how long the
        // downstream consumer holds an already-yielded update before asking for the
        // next one. Two substantive chunks arm and then satisfy the guard once; the
        // consumer then sits on chunk2 far longer than the inactivity window before
        // requesting chunk3 — that gap must not be held against the provider, which
        // answers chunk3 promptly once asked.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var leaf = new FakeChatClient(streamHandler: (_, _, ct) => YieldThreeQuickly(ct));
        var client = new StreamStallGuardChatClient(leaf, Policy, NullLogger.Instance, time);

        await using var enumerator = client
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync()); // chunk1 — arms the guard
        Assert.True(await enumerator.MoveNextAsync()); // chunk2 — the wait for this one was itself timer-guarded

        // Consumer holds chunk2 far longer than the inactivity window before asking
        // for chunk3. The timer that guarded the (already-satisfied) wait for chunk2
        // must have been disarmed on arrival — it must not still be counting down.
        time.Advance(Policy.StreamInactivityTimeout * 10);

        Assert.True(await enumerator.MoveNextAsync()); // chunk3 — must still succeed, not a false abort
    }

    [Fact]
    public async Task ZeroTimeout_DisablesTheGuard()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var leaf = new FakeChatClient(streamHandler: (_, _, ct) => YieldTwoThenStallForever(ct));
        var client = new StreamStallGuardChatClient(
            leaf, new RetryPolicy { StreamInactivityTimeout = TimeSpan.Zero }, NullLogger.Instance, time);

        // Owns cancellation directly (rather than TestContext.Current.CancellationToken)
        // so the still-pending read below can be unwound deterministically instead of
        // leaving a MoveNextAsync call in flight when the enumerator is disposed.
        using var cts = new CancellationTokenSource();
        var enumerator = client
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token)
            .GetAsyncEnumerator(cts.Token);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());

        var pending = enumerator.MoveNextAsync().AsTask();
        time.Advance(Policy.StreamInactivityTimeout * 100);
        Assert.False(pending.IsCompleted); // disabled — nothing ever cancels this read

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await enumerator.DisposeAsync();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldTwoThenStallForever(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("chunk1")] };
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("chunk2")] };

        // Dead/half-open connection: no more tokens, no error, no close.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break; // unreachable — Task.Delay above only returns via cancellation
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> SlowButProgressing(
        TimeProvider time, TimeSpan gap, int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Delay(gap, time, cancellationToken);
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent($"chunk{i}")] };
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> DelayThenYieldOnce(
        TimeProvider time, TimeSpan delay,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(delay, time, cancellationToken);
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("first")] };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> KeepaliveTwiceThenDelayedSubstantive(
        TimeProvider time, TimeSpan delay,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A content-free keepalive: no text/thinking/tool-call content and no finish
        // reason, matching ChatStreamUpdateClassifier.IsSubstantiveUpdate's "false" case.
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [] };
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [] };

        await Task.Delay(delay, time, cancellationToken);
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("first substantive")] };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldThreeQuickly(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("chunk1")] };
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("chunk2")] };
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("chunk3")] };
    }
}
