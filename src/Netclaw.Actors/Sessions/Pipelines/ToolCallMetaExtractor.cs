// -----------------------------------------------------------------------
// <copyright file="ToolCallMetaExtractor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Extracts <see cref="ToolCallMeta"/> fields from a <see cref="FunctionCallContent"/>
/// and returns a cleaned tool call with meta keys removed.
/// </summary>
internal static class ToolCallMetaExtractor
{
    /// <param name="resolveMeta">
    /// Maps a key to its canonical meta field (schema-aware for the executor,
    /// exact for persistence). Defaults to exact. See
    /// <see cref="ToolCallMeta.ExtractFrom"/>.
    /// </param>
    public static (ToolCallMeta? Meta, FunctionCallContent CleanedToolCall) Extract(
        FunctionCallContent tc, Func<string, string?>? resolveMeta = null)
    {
        var (meta, cleanArgs) = ToolCallMeta.ExtractFrom(tc.Arguments, resolveMeta);
        if (meta is null)
            return (null, tc);

        var cleanedTc = new FunctionCallContent(tc.CallId, tc.Name, cleanArgs);
        return (meta, cleanedTc);
    }

    /// <summary>
    /// Rejects present-but-invalid meta values before dispatch. Returns null when
    /// the meta surface is valid; otherwise a model-facing error (the call must
    /// not execute — the agent expressed execution semantics we cannot honor, so
    /// we do not run on defaults instead). Computed pipeline-side so the
    /// persisted <see cref="ToolCallMeta"/> type stays unchanged. Key resolution
    /// is spelling-tolerant via <see cref="ToolArgumentHelper.ResolveMetaField"/>,
    /// mirroring <see cref="ToolCallMeta.ExtractFrom"/>, so a mis-named-but-invalid
    /// value (e.g. <c>TimeoutSeconds:"abc"</c>) is rejected loudly rather than
    /// silently dropped at extraction.
    /// </summary>
    public static string? ValidateMetaValues(
        IDictionary<string, object?>? arguments, Func<string, string?>? resolveMeta = null)
    {
        if (arguments is null || arguments.Count == 0)
            return null;

        resolveMeta ??= ToolCallMeta.ResolveExactMetaField;

        // Validity is defined as "the shared coercion accepts it" — the same
        // TryCoerce* ToolCallMeta.ExtractFrom binds through — so a value can
        // never validate here yet extract to null (or vice versa). A timeout
        // additionally must be positive, matching ExtractFrom's `> 0` guard.
        // Resolution mirrors ExtractFrom (the same resolveMeta) so a near-miss
        // that extraction would consume is the same one validated here. The error
        // names the model's own key spelling so the correction is clear.
        foreach (var kvp in arguments)
        {
            var canonical = resolveMeta(kvp.Key);
            if (canonical is null
                || kvp.Value is null or JsonElement { ValueKind: JsonValueKind.Null })
                continue;

            switch (canonical)
            {
                case "_timeout_seconds" when !(ToolArgumentHelper.TryCoerceInt(kvp.Value, out var t) && t > 0):
                    return $"Error: Meta argument '{kvp.Key}' value '{ToolArgumentHelper.RenderValue(kvp.Value)}' is not a valid positive integer. The tool was NOT executed.";

                case "_background" when !ToolArgumentHelper.TryCoerceBool(kvp.Value, out _):
                    return $"Error: Meta argument '{kvp.Key}' value '{ToolArgumentHelper.RenderValue(kvp.Value)}' is not a valid boolean. The tool was NOT executed.";
            }
        }

        return null;
    }
}
