// -----------------------------------------------------------------------
// <copyright file="HeadlessChannelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests;

/// <summary>
/// Covers C1: the discard-and-resume mechanism (<c>LlmSessionActor.TryResumeAfterTimeout</c>)
/// emits <see cref="TextStreamDiscarded"/> before a resumed call streams its own
/// deltas. <see cref="HeadlessChannel"/> accumulates <see cref="TextDeltaOutput"/>
/// text into its JSON envelope response buffer — without honoring the discard
/// signal, a dead call's partial text glues onto the resumed call's answer. These
/// tests drive <see cref="HeadlessChannel.HandleOutput"/> directly (an internal
/// test seam) with a real multi-delta stall, then assert on the DELTA-accumulated
/// buffer, not <see cref="TextOutput"/>.
/// </summary>
public sealed class HeadlessChannelTests
{
    private static HeadlessChannel CreateChannel(bool jsonOutput) => new(
        new DaemonClient("http://127.0.0.1:1"), // never dialed in this test
        new NetclawPaths(),
        new FakeApplicationLifetime(),
        TimeProvider.System,
        new HeadlessOptions("test prompt") { JsonOutput = jsonOutput },
        NullLogger<HeadlessChannel>.Instance);

    [Fact]
    public void TextStreamDiscarded_clears_json_envelope_buffer_between_dead_and_resumed_deltas()
    {
        var channel = CreateChannel(jsonOutput: true);
        var sessionId = new SessionId("headless/test");

        // Real multi-delta stall — two substantive deltas before discard, matching
        // a genuine half-open provider stream (a single delta would not exercise
        // the buffered-first-delta path the way a real stall does).
        channel.HandleOutput(new TextDeltaOutput("stalled chunk one ") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextDeltaOutput("STALLED_PARTIAL_MARKER") { SessionId = sessionId }, null);

        channel.HandleOutput(new TextStreamDiscarded { SessionId = sessionId }, null);

        channel.HandleOutput(new TextDeltaOutput("Resumed answer ") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextDeltaOutput("after timeout") { SessionId = sessionId }, null);

        // The JSON envelope's Response field is built from this buffer — it must
        // contain ONLY the resumed call's text.
        Assert.Equal("Resumed answer after timeout", channel.ResponseBufferForTesting);
        Assert.DoesNotContain("STALLED_PARTIAL_MARKER", channel.ResponseBufferForTesting, StringComparison.Ordinal);
    }

    [Fact]
    public void TextStreamDiscarded_is_a_no_op_when_no_deltas_streamed_yet()
    {
        var channel = CreateChannel(jsonOutput: true);
        var sessionId = new SessionId("headless/test-empty");

        channel.HandleOutput(new TextStreamDiscarded { SessionId = sessionId }, null);
        channel.HandleOutput(new TextDeltaOutput("first answer") { SessionId = sessionId }, null);

        Assert.Equal("first answer", channel.ResponseBufferForTesting);
    }

    [Fact]
    public void TextStreamDiscarded_preserves_an_earlier_completed_calls_text_but_discards_only_the_dead_calls_partial()
    {
        // D1: the JSON envelope buffer was TURN-scoped, so TextStreamDiscarded's
        // Clear() wiped an earlier COMPLETED call's already-delivered text along
        // with the dead call's partial. This mirrors ChatPage's call-scoped
        // segment semantics: TextOutput marks a call boundary and commits
        // everything before it, so a later discard can only ever remove text
        // from the call that is actually dying.
        var channel = CreateChannel(jsonOutput: true);
        var sessionId = new SessionId("headless/preamble-preserved");

        // Call 1: streams a preamble, then completes (a tool round).
        channel.HandleOutput(new TextDeltaOutput("Checking the files now. ") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextOutput("Checking the files now. ") { SessionId = sessionId }, null);

        // Call 2: streams two real deltas, then dies mid-stream and is discarded.
        channel.HandleOutput(new TextDeltaOutput("stalled chunk one ") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextDeltaOutput("STALLED_PARTIAL_MARKER") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextStreamDiscarded { SessionId = sessionId }, null);

        // Resumed call streams the real final answer.
        channel.HandleOutput(new TextDeltaOutput("Done: the answer is X.") { SessionId = sessionId }, null);

        Assert.Equal("Checking the files now. Done: the answer is X.", channel.ResponseBufferForTesting);
        Assert.DoesNotContain("STALLED_PARTIAL_MARKER", channel.ResponseBufferForTesting, StringComparison.Ordinal);
    }

    [Fact]
    public void TextStreamDiscarded_lets_TextOutput_repopulate_after_discard()
    {
        // D2: the discard arm cleared _responseBuffer but left
        // _receivedTextDeltaInCurrentTurn true, so a resumed call that never
        // streams a delta (SessionLlmInvoker withholds the first delta until a
        // second arrives — a single-chunk response never gets a second) hit the
        // "already streamed this turn" branch of the TextOutput case and skipped
        // the append entirely. A successful turn then reported an EMPTY response
        // — worse than the pre-fix loud failure.
        var channel = CreateChannel(jsonOutput: true);
        var sessionId = new SessionId("headless/single-chunk-resume");

        channel.HandleOutput(new TextDeltaOutput("stalled") { SessionId = sessionId }, null);
        channel.HandleOutput(new TextStreamDiscarded { SessionId = sessionId }, null);

        // No further deltas — the resumed call's answer arrives as a single,
        // non-streamed TextOutput.
        channel.HandleOutput(new TextOutput("Resumed answer") { SessionId = sessionId }, null);

        Assert.Equal("Resumed answer", channel.ResponseBufferForTesting);
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() { }
    }
}
