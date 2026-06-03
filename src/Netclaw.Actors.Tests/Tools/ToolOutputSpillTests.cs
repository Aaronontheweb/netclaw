// -----------------------------------------------------------------------
// <copyright file="ToolOutputSpillTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ToolOutputSpillTests : IDisposable
{
    private readonly string _sessionDir =
        Path.Combine(Path.GetTempPath(), "nc-spill-" + Guid.NewGuid().ToString("N"));

    public ToolOutputSpillTests() => Directory.CreateDirectory(_sessionDir);

    public void Dispose()
    {
        if (Directory.Exists(_sessionDir))
            Directory.Delete(_sessionDir, recursive: true);
    }

    private ToolExecutionContext Context(string callId, int budget) =>
        new("session/thread", _sessionDir)
        {
            Audience = TrustAudience.Personal,
            ToolCallId = new ToolCallId(callId),
            MaxInlineToolResultChars = budget,
        };

    private string ToolCallsDir => Path.Combine(_sessionDir, "tool-calls");

    [Fact]
    public async Task Under_budget_returns_redacted_without_spilling()
    {
        var captured = new string('a', 50);
        var result = await ToolOutputSpill.RenderAsync(
            captured, ceilingExceeded: false, Context("call_1", budget: 100), captureMax: 1000, CancellationToken.None);

        Assert.Equal(captured, result);
        Assert.False(Directory.Exists(ToolCallsDir)); // nothing spilled
    }

    [Fact]
    public async Task Over_budget_spills_full_output_and_steers()
    {
        var captured = new string('H', 200) + new string('T', 200); // 400 > 100
        var result = await ToolOutputSpill.RenderAsync(
            captured, ceilingExceeded: false, Context("call_2", budget: 100), captureMax: 1000, CancellationToken.None);

        var spillPath = Path.Combine(ToolCallsDir, "call_2.log");
        Assert.True(File.Exists(spillPath));
        Assert.Equal(captured, await File.ReadAllTextAsync(spillPath, CancellationToken.None));   // full output on disk
        Assert.StartsWith(new string('H', 50), result);                  // inline head
        Assert.Contains("full output saved to", result);
        Assert.Contains(spillPath, result);
        Assert.Contains("file_read", result);
        Assert.Contains("grep", result);
    }

    [Fact]
    public async Task Spill_file_is_redacted_on_write()
    {
        var captured = "API_KEY=supersecret123\n" + new string('x', 200);
        var result = await ToolOutputSpill.RenderAsync(
            captured, ceilingExceeded: false, Context("call_3", budget: 50), captureMax: 1000, CancellationToken.None);

        var onDisk = await File.ReadAllTextAsync(Path.Combine(ToolCallsDir, "call_3.log"), CancellationToken.None);
        Assert.DoesNotContain("supersecret123", onDisk);   // redacted before write
        Assert.Contains("REDACTED", onDisk);
        Assert.DoesNotContain("supersecret123", result);   // and inline too
    }

    [Fact]
    public async Task Ceiling_exceeded_adds_note()
    {
        var captured = new string('x', 400);
        var result = await ToolOutputSpill.RenderAsync(
            captured, ceilingExceeded: true, Context("call_4", budget: 100), captureMax: 32000, CancellationToken.None);

        Assert.Contains("capture ceiling", result);
        Assert.Contains("32000", result);
    }

    [Fact]
    public async Task No_session_directory_degrades_to_inline_only()
    {
        var ctx = new ToolExecutionContext("session/thread", sessionDirectory: null)
        {
            Audience = TrustAudience.Personal,
            ToolCallId = new ToolCallId("call_5"),
            MaxInlineToolResultChars = 100,
        };
        var captured = new string('H', 200) + new string('T', 200);

        var result = await ToolOutputSpill.RenderAsync(
            captured, ceilingExceeded: false, ctx, captureMax: 1000, CancellationToken.None);

        Assert.StartsWith(new string('H', 50), result);    // inline still produced
        Assert.DoesNotContain("saved to", result);          // but no spill path
    }

    [Fact]
    public async Task Unsafe_call_id_cannot_escape_tool_calls_directory()
    {
        var captured = new string('H', 200) + new string('T', 200);
        await ToolOutputSpill.RenderAsync(
            captured, ceilingExceeded: false, Context("../../evil", budget: 100), captureMax: 1000, CancellationToken.None);

        // The traversal is sanitized away: exactly one file, inside tool-calls,
        // and nothing written to the session dir's parent.
        var written = Directory.GetFiles(ToolCallsDir);
        Assert.Single(written);
        Assert.StartsWith(ToolCallsDir, Path.GetFullPath(written[0]));
        Assert.DoesNotContain("evil.log", Directory.GetFiles(Directory.GetParent(_sessionDir)!.FullName));
    }
}
