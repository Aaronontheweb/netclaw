// -----------------------------------------------------------------------
// <copyright file="ToolOutputSpill.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Turns a bounded tool-output capture into the model-facing result: redacts it,
/// and when it exceeds the inline budget <c>N</c>
/// (<see cref="ToolExecutionContext.MaxInlineToolResultChars"/>), returns an
/// <c>N</c>-char head+tail window plus a pointer to the full (redacted) output
/// spilled at <c>{SessionDirectory}/tool-calls/{ToolCallId}.log</c>, steering the
/// model to read a slice or grep it rather than re-run the tool.
/// </summary>
internal static class ToolOutputSpill
{
    private const string ToolCallsSubdirectory = "tool-calls";

    /// <summary>
    /// Renders the inline tool result from <paramref name="captured"/> (the bounded
    /// capture produced by <see cref="BoundedOutputReader.DrainToWindowAsync"/>).
    /// Redaction is applied once over the whole bounded buffer (so multi-line
    /// secrets survive), then the inline window is taken from the redacted text —
    /// so what the model sees inline and what lands in the spill file are both
    /// redacted.
    /// </summary>
    /// <param name="captured">The bounded capture (already ceiling-limited).</param>
    /// <param name="ceilingExceeded">True if output exceeded the capture ceiling.</param>
    /// <param name="context">Carries the inline budget, session dir, and call id.</param>
    /// <param name="captureMax">The capture ceiling, for the ceiling-note text.</param>
    public static async Task<string> RenderAsync(
        string captured, bool ceilingExceeded, ToolExecutionContext context, int captureMax, CancellationToken ct)
    {
        var redacted = SecretOutputRedactor.Redact(captured);
        var budget = context.MaxInlineToolResultChars;

        // No inline budget (sub-agent / Empty context) or it already fits: the
        // capture ceiling already bounds memory, so return the redacted capture.
        if (budget <= 0 || redacted.Length <= budget)
            return redacted;

        var inline = BoundedOutputReader.Window(redacted, budget);
        var spillPath = await TryWriteSpillAsync(redacted, context, ct);
        return Compose(inline, spillPath, ceilingExceeded, captureMax);
    }

    private static async Task<string?> TryWriteSpillAsync(string redacted, ToolExecutionContext context, CancellationToken ct)
    {
        // A spill needs both a place (session dir) and a name (call id). Without
        // either — direct-construction / Empty contexts — degrade to inline-only.
        if (string.IsNullOrWhiteSpace(context.SessionDirectory) || context.ToolCallId is not { } callId)
            return null;

        try
        {
            var dir = Path.Combine(context.SessionDirectory, ToolCallsSubdirectory);
            Directory.CreateDirectory(dir);
            // Sanitize the (provider-supplied) call id before using it as a file
            // name so it can never escape the tool-calls directory.
            var path = Path.Combine(dir, SafeFileName(callId.Value) + ".log");
            await File.WriteAllTextAsync(path, redacted, ct);
            return path;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or System.Security.SecurityException)
        {
            // Best-effort: the inline head+tail is still returned. A failed on-disk
            // copy must not fail the tool call.
            Debug.WriteLine($"tool-output spill write failed: {ex.Message}");
            return null;
        }
    }

    private static string Compose(string inline, string? spillPath, bool ceilingExceeded, int captureMax)
    {
        var sb = new StringBuilder(inline);
        sb.Append("\n\n[output truncated to the inline budget");
        if (spillPath is not null)
            sb.Append($"; full output saved to {spillPath} — read a slice with file_read (offset/limit) or grep it instead of re-running");
        if (ceilingExceeded)
            sb.Append($"; output also exceeded the {captureMax}-char capture ceiling, so even the saved copy is a head+tail view");
        sb.Append(']');
        return sb.ToString();
    }

    private static string SafeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = id.Length <= 256 ? stackalloc char[id.Length] : new char[id.Length];
        for (var i = 0; i < id.Length; i++)
            buffer[i] = invalid.Contains(id[i]) ? '_' : id[i];
        var safe = new string(buffer);
        return string.IsNullOrWhiteSpace(safe) ? "tool-call" : safe;
    }
}
