// -----------------------------------------------------------------------
// <copyright file="BoundedOutputReaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class BoundedOutputReaderTests
{
    // ── DrainToWindowAsync ──

    [Fact]
    public async Task DrainToWindow_short_output_returned_verbatim()
    {
        var input = "hello world";
        var (text, truncated) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(input), 100, CancellationToken.None);
        Assert.Equal(input, text);
        Assert.False(truncated);
    }

    [Fact]
    public async Task DrainToWindow_empty_input_returns_empty()
    {
        var (text, truncated) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(""), 100, CancellationToken.None);
        Assert.Equal("", text);
        Assert.False(truncated);
    }

    [Fact]
    public async Task DrainToWindow_output_exactly_at_cap_not_truncated()
    {
        var input = new string('a', 100);
        var (text, truncated) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(input), 100, CancellationToken.None);
        Assert.Equal(input, text);
        Assert.False(truncated);
    }

    [Fact]
    public async Task DrainToWindow_long_output_truncated_with_head_and_tail()
    {
        // 100-char head marker + separator + 100-char tail marker, with filler in the middle
        var head = new string('H', 100);
        var middle = new string('M', 5000);
        var tail = new string('T', 100);
        var input = head + middle + tail;

        var (text, truncated) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(input), 200, CancellationToken.None);

        Assert.True(truncated);
        Assert.StartsWith(new string('H', 100), text);  // head preserved
        Assert.EndsWith(new string('T', 100), text);    // tail preserved
        Assert.Contains("...", text);                    // separator present
        Assert.DoesNotContain("M", text);                // middle discarded
    }

    [Fact]
    public async Task DrainToWindow_head_and_tail_split_evenly()
    {
        // budget=10 → headCap=5, tailCap=5
        var input = "AAAAAXXXXXXBBBBB"; // 16 chars: 5 head, 6 overflow discard, 5 tail
        var (text, truncated) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(input), 10, CancellationToken.None);

        Assert.True(truncated);
        Assert.StartsWith("AAAAA", text);
        Assert.EndsWith("BBBBB", text);
    }

    [Fact]
    public async Task DrainToWindow_disabled_cap_returns_full_output()
    {
        var input = new string('x', 10_000);
        var (text, truncated) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(input), 0, CancellationToken.None);
        Assert.Equal(input, text);
        Assert.False(truncated);
    }

    [Fact]
    public async Task DrainToWindow_tail_ring_wraps_across_small_chunks()
    {
        // Drives the ring's wraparound + start-advance path that the StringReader
        // tests skip: each read delivers a chunk smaller than tailCap, so the tail
        // window is rebuilt incrementally and must wrap rather than reset wholesale.
        // budget=10 → headCap=5 ("ABCDE"), tailCap=5; last 5 of "FGHIJKLMNO" = "KLMNO".
        var reader = new ChunkedReader("ABCDEFGHIJKLMNO", chunkSize: 3);

        var (text, truncated) = await BoundedOutputReader.DrainToWindowAsync(reader, 10, CancellationToken.None);

        Assert.True(truncated);
        Assert.Equal("ABCDE\n...\nKLMNO", text);
    }

    // ── Window (pure string head+tail) ──

    [Fact]
    public void Window_under_budget_returned_unchanged()
    {
        Assert.Equal("short", BoundedOutputReader.Window("short", 100));
    }

    [Fact]
    public void Window_over_budget_keeps_head_and_tail()
    {
        var input = new string('H', 50) + new string('M', 500) + new string('T', 50);
        var result = BoundedOutputReader.Window(input, 100);

        Assert.StartsWith(new string('H', 50), result);
        Assert.EndsWith(new string('T', 50), result);
        Assert.DoesNotContain("M", result);
    }

    // ── DrainCaptureAsync ──

    [Fact]
    public async Task DrainCapture_under_inline_budget_inline_equals_captured()
    {
        var input = new string('a', 50);
        var (captured, inline, truncated, ceiling) = await BoundedOutputReader.DrainCaptureAsync(
            new StringReader(input), captureMax: 1000, inlineBudget: 100, CancellationToken.None);

        Assert.Equal(input, captured);
        Assert.Equal(input, inline);
        Assert.False(truncated);   // under the inline budget — no spill needed
        Assert.False(ceiling);
    }

    [Fact]
    public async Task DrainCapture_between_inline_and_ceiling_flags_truncated_not_ceiling()
    {
        // 400 chars: over the 100 inline budget, under the 1000 capture ceiling.
        var input = new string('H', 200) + new string('T', 200);
        var (captured, inline, truncated, ceiling) = await BoundedOutputReader.DrainCaptureAsync(
            new StringReader(input), captureMax: 1000, inlineBudget: 100, CancellationToken.None);

        Assert.Equal(input, captured);            // full output captured for spill
        Assert.True(truncated);                   // inline dropped data → spill warranted
        Assert.False(ceiling);                    // but still under the capture ceiling
        Assert.True(inline.Length < captured.Length);
        Assert.StartsWith(new string('H', 50), inline);
        Assert.EndsWith(new string('T', 50), inline);
    }

    [Fact]
    public async Task DrainCapture_over_ceiling_flags_truncated_and_ceiling()
    {
        var input = new string('x', 5000);
        var (captured, _, truncated, ceiling) = await BoundedOutputReader.DrainCaptureAsync(
            new StringReader(input), captureMax: 1000, inlineBudget: 100, CancellationToken.None);

        Assert.True(truncated);
        Assert.True(ceiling);
        Assert.Contains("...", captured);         // capture itself is head+tail
    }

    [Fact]
    public async Task DrainCapture_nonpositive_capture_ceiling_throws()
    {
        // The capture path must stay bounded — it must NOT inherit the window's
        // 0-disables-cap opt-out, which would buffer the whole stream and risk OOM.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BoundedOutputReader.DrainCaptureAsync(
                new StringReader("anything"), captureMax: 0, inlineBudget: 100, CancellationToken.None));
    }

    [Fact]
    public async Task DrainCapture_inline_budget_clamped_to_capture_ceiling()
    {
        // inlineBudget > captureMax is meaningless and would otherwise slice through
        // the capture's own separator; it is clamped so inline never exceeds capture.
        var input = new string('H', 2500) + new string('T', 2500);
        var (captured, inline, _, _) = await BoundedOutputReader.DrainCaptureAsync(
            new StringReader(input), captureMax: 20, inlineBudget: 1000, CancellationToken.None);

        // Clamped to captureMax: inline is a clean head+tail, identical to the
        // capture — not a re-window that splices through the capture's separator.
        Assert.Equal("HHHHHHHHHH\n...\nTTTTTTTTTT", captured);
        Assert.Equal(captured, inline);
    }

    // Hands out at most chunkSize chars per read so tests can exercise the tail
    // ring's incremental wrap path — real pipe reads arrive in arbitrary slices,
    // not the single 4KB gulp a StringReader gives.
    private sealed class ChunkedReader(string data, int chunkSize) : TextReader
    {
        private int _pos;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = data.Length - _pos;
            if (remaining <= 0)
                return ValueTask.FromResult(0);

            var n = Math.Min(Math.Min(chunkSize, buffer.Length), remaining);
            data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return ValueTask.FromResult(n);
        }
    }
}
