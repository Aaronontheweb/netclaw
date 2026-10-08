// -----------------------------------------------------------------------
// <copyright file="ToolOutputSpill.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Microsoft.Extensions.Logging;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Bounds a tool result to the inline budget <c>N</c>
/// (<see cref="ToolExecutionContext.MaxInlineToolResultChars"/>) and, when it
/// exceeds <c>N</c>, spills the full result to the current session and steers
/// the model to continue through <c>tool_output_read</c> by opaque call id.
/// When the spill cannot be written, the result text says that the full output
/// was not kept, and a warning goes to the log.
/// </summary>
/// <remarks>
/// Called from <c>DispatchingToolExecutor</c> for <i>every</i> tool, right after
/// the central redaction — so bounding + spill happen once, uniformly, for the
/// main session and sub-agents alike (both funnel through the dispatcher). Tools
/// only bound their own <i>capture</i> for memory safety; they do not window or
/// spill. The two-param overload accepts separate model-facing and spill content
/// so that tools with <c>SuppressOutputRedaction</c> can return raw results to the
/// model while still writing redacted content to the spill file on disk.
/// </remarks>
internal static class ToolOutputSpill
{
    // Content budget used when neither the tool nor the context supplies one
    // (sub-agent / Empty / direct construction). Matches
    // SessionTuning.MaxInlineToolResultChars's default so un-plumbed paths bound the
    // same as the main session.
    internal const int DefaultContentBudget = 12_000;

    /// <summary>
    /// Returns <paramref name="redactedResult"/> unchanged if it fits
    /// <paramref name="budget"/>; otherwise returns a <paramref name="budget"/>-char
    /// head+tail window plus a steer, having spilled the full result to a session
    /// file. The dispatcher resolves <paramref name="budget"/> from the tool's
    /// per-tool override or the session content budget.
    /// </summary>
    public static Task<string> BoundAndSpillAsync(
        string redactedResult, string? toolCallId, int budget, ToolInvocationContext context,
        ILogger logger, CancellationToken ct)
        => BoundAndSpillAsync(modelFacingResult: redactedResult, spillContent: redactedResult,
            toolCallId, budget, context, logger, ct);

    /// <summary>
    /// Overload that separates the model-facing result from the spill content.
    /// When a tool suppresses output redaction, <paramref name="modelFacingResult"/>
    /// is the raw (unredacted) result while <paramref name="spillContent"/> is the
    /// redacted version written to disk.
    /// </summary>
    public static async Task<string> BoundAndSpillAsync(
        string modelFacingResult, string spillContent, string? toolCallId, int budget,
        ToolInvocationContext context, ILogger logger, CancellationToken ct)
    {
        if (budget <= 0)
            budget = DefaultContentBudget;

        if (modelFacingResult.Length <= budget)
            return modelFacingResult;

        var inline = BoundedOutputReader.Window(modelFacingResult, budget);
        var spill = await TryWriteSpillAsync(spillContent, toolCallId, context, ct);
        if (spill.Failure is { } failure)
        {
            // The model loses the middle of this result and cannot read it again.
            // The result text says so; this record tells the operator why.
            logger.LogWarning(
                "tool_output_spill_not_retained reason={Reason} sessionId={SessionId} callId={CallId} fullLength={FullLength} budget={Budget} detail={Detail}",
                failure.Reason,
                context?.SessionId,
                toolCallId,
                modelFacingResult.Length,
                budget,
                failure.Detail);
        }

        return Compose(inline, spill, modelFacingResult.Length, budget);
    }

    private static async Task<SpillOutcome> TryWriteSpillAsync(
        string redacted, string? toolCallId, ToolInvocationContext context, CancellationToken ct)
    {
        // A spill needs a place (the session workspace folder) and a name (the call id).
        if (context is null || string.IsNullOrWhiteSpace(context.SessionDirectory))
            return SpillOutcome.NotRetained(SpillFailureReason.NoSessionFolder);

        if (!ToolOutputSpillLocation.IsValidCallId(toolCallId))
            return SpillOutcome.NotRetained(SpillFailureReason.UnusableCallId);

        // A failed (or cancelled) on-disk copy must not fail the tool call — the
        // inline head+tail is always returned — so the write is decoupled from the
        // request's CancellationToken (the body is bounded by the capture ceiling,
        // so the write is small and fast). The `ct` is kept in the signature for
        // symmetry / future use.
        _ = ct;
        try
        {
            // The session workspace folder is created on first use. A shell launch
            // creates it, and so does this spill: a session whose first large
            // result comes from another tool (skill_load, an MCP tool) has no
            // folder yet. Without this step that result got no continuation.
            if (!ToolOutputSpillLocation.TryEnsureSessionDirectory(context.SessionDirectory))
                return SpillOutcome.NotRetained(SpillFailureReason.UnsafeSessionFolder);

            if (!ToolOutputSpillLocation.TryResolve(
                    context.SessionDirectory,
                    toolCallId,
                    out var directory,
                    out var path))
            {
                return SpillOutcome.NotRetained(SpillFailureReason.UnsafeSessionFolder);
            }

            if (!ToolOutputSpillLocation.IsSafeForIo(context.SessionDirectory!, path))
                return SpillOutcome.NotRetained(SpillFailureReason.UnsafeSessionFolder);

            Directory.CreateDirectory(directory);
            if (!ToolOutputSpillLocation.IsSafeForIo(context.SessionDirectory!, path))
                return SpillOutcome.NotRetained(SpillFailureReason.UnsafeSessionFolder);

            await File.WriteAllTextAsync(path, redacted, CancellationToken.None);
            return SpillOutcome.Retained(toolCallId!);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or System.Security.SecurityException)
        {
            return SpillOutcome.NotRetained(SpillFailureReason.WriteFailed, ex.Message);
        }
    }

    private static string Compose(string inline, SpillOutcome spill, int fullLength, int budget)
    {
        var sb = new StringBuilder(inline);
        sb.Append($"\n\n[output truncated to {budget} chars of {fullLength}");
        if (spill.CallId is not null)
        {
            sb.Append($"; continue with tool_output_read using CallId='{spill.CallId}' and a bounded Start/Limit window instead of re-running");
        }
        else
        {
            // No silent fallback: the model must know that the middle is gone and
            // that tool_output_read has nothing for this call. The text names no path.
            sb.Append("; Netclaw did not keep the full output, so tool_output_read cannot continue this call. ");
            sb.Append("Run the source tool again with narrower output bounds");
        }
        sb.Append(']');
        return sb.ToString();
    }

    private enum SpillFailureReason
    {
        NoSessionFolder,
        UnusableCallId,
        UnsafeSessionFolder,
        WriteFailed,
    }

    private sealed record SpillFailure(SpillFailureReason Reason, string? Detail);

    private readonly record struct SpillOutcome(string? CallId, SpillFailure? Failure)
    {
        public static SpillOutcome Retained(string callId) => new(callId, null);

        public static SpillOutcome NotRetained(SpillFailureReason reason, string? detail = null)
            => new(null, new SpillFailure(reason, detail));
    }
}
