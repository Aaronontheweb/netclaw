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
}
