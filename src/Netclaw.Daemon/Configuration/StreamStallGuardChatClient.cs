// -----------------------------------------------------------------------
// <copyright file="StreamStallGuardChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Decorates an <see cref="IChatClient"/> so a streaming response that goes silent
/// mid-stream aborts in seconds instead of waiting on the coarse per-call watchdog
/// (minutes). A dead or half-open provider connection can leave the socket open
/// with no more tokens, no error, and no close — the actor-level watchdog still
/// catches this eventually, but only after burning most of its budget.
/// <para>
/// Only the gap <b>after</b> the first <i>substantive</i> update is bounded — the
/// same distinction <see cref="ChatStreamUpdateClassifier.IsSubstantiveUpdate"/> and
/// <c>ProcessingWatchdog</c> already draw, reused here rather than re-derived. Time to
/// first substantive output is left to the existing, more generous per-call watchdog,
/// because a self-hosted backend can be legitimately silent for minutes during cold
/// prefill. A reasoning delta with text arms the tight budget, the same as a text or
/// tool-call delta. Only a content-free keepalive — no text, no thinking, no tool
/// call, and no finish reason — leaves the budget unarmed. Once armed, every later
/// update — including a content-free keepalive — resets the inactivity clock, so a
/// slow-but-alive stream is never falsely aborted.
/// </para>
/// <para>
/// The timer measures provider silence only. It is disarmed immediately after each
/// update arrives and re-armed only just before asking for the next one, so time a
/// downstream consumer spends holding an already-yielded update is never counted
/// against the provider.
/// </para>
/// <para>
/// Sits directly below <see cref="RetryingChatClient"/> in the composed pipeline
/// (<see cref="PipelineChatClientFactory.Compose"/>). By construction a stall this
/// guard catches has already yielded at least one chunk, so <see cref="RetryingChatClient"/>'s
/// pre-first-chunk retry cannot re-issue the request (the partial output already
/// streamed cannot be un-sent) — the <see cref="TimeoutException"/> propagates to the
/// caller. No new retry mechanism: the exception is a plain <see cref="TimeoutException"/>,
/// so it is classified by the same <see cref="RetryPolicy.ShouldRetry"/> rule and reaches
/// the actor's existing failure handling exactly as any other transient failure would —
/// just in seconds instead of minutes, leaving the caller's own retry budget intact.
/// </para>
/// </summary>
public sealed class StreamStallGuardChatClient : DelegatingChatClient
{
    private readonly TimeSpan _inactivityTimeout;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public StreamStallGuardChatClient(
        IChatClient innerClient,
        RetryPolicy policy,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : base(innerClient)
    {
        _inactivityTimeout = policy.StreamInactivityTimeout;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_inactivityTimeout <= TimeSpan.Zero)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return update;
            yield break;
        }

        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Not armed until the first substantive update arrives (see class remarks) —
        // the initial due time is infinite.
        using var timer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            stallCts,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        var enumerator = base.GetStreamingResponseAsync(messages, options, stallCts.Token)
            .GetAsyncEnumerator(stallCts.Token);
        var armed = false;
        try
        {
            while (true)
            {
                if (armed)
                    timer.Change(_inactivityTimeout, Timeout.InfiniteTimeSpan);

                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && stallCts.IsCancellationRequested)
                {
                    // The caller's own token is still live — this cancellation came from
                    // our timer, not a real abort. Surface it as a plain TimeoutException
                    // so it flows through RetryPolicy.ShouldRetry (already retryable) and
                    // the actor's existing TimeoutException -> ErrorCategory.Timeout
                    // classification, unchanged.
                    _logger.LogWarning(
                        "LLM stream stalled — no update for {TimeoutSeconds:F0}s after the first substantive delta, aborting",
                        _inactivityTimeout.TotalSeconds);
                    throw new TimeoutException(
                        $"LLM stream produced no update for {_inactivityTimeout.TotalSeconds:F0}s after the first substantive delta (stall detected)");
                }

                // The provider produced something (or ended cleanly) within the window —
                // disarm before yielding so the timer never also measures how long the
                // downstream consumer holds this update. It is re-armed above, just
                // before the next MoveNextAsync, so it measures provider silence only.
                timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                if (!hasNext)
                    yield break;

                if (!armed && ChatStreamUpdateClassifier.IsSubstantiveUpdate(enumerator.Current))
                    armed = true;

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
