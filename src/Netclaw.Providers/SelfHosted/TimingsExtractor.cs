// -----------------------------------------------------------------------
// <copyright file="TimingsExtractor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Extracts backend-specific per-request timing and cache telemetry from
/// an OpenAI-compatible chat completion response. Implementations are
/// applied in sequence on every response — field paths are
/// non-overlapping across supported backends so multiple extractors can
/// safely run without conflict.
/// </summary>
internal interface ITimingsExtractor
{
    /// <summary>
    /// Reads any backend-specific telemetry from <paramref name="root"/>
    /// and writes it into <paramref name="details"/>. Implementations
    /// SHALL be no-ops when their expected field paths are absent.
    /// </summary>
    void Extract(JsonElement root, UsageDetails details);
}

/// <summary>
/// llama.cpp ships a top-level <c>timings</c> sibling to <c>usage</c>.
/// <c>cache_n</c> maps to <see cref="UsageDetails.CachedInputTokenCount"/>;
/// timing/throughput fields are integer-encoded into
/// <see cref="UsageDetails.AdditionalCounts"/> via the integer-scaled
/// keys declared in <see cref="TimingsKeys"/> (microseconds for latency,
/// ×100 for tokens-per-second) to fit the <c>long</c>-typed dictionary.
/// </summary>
internal sealed class LlamaCppTimingsExtractor : ITimingsExtractor
{
    public void Extract(JsonElement root, UsageDetails details)
    {
        if (!root.TryGetProperty("timings", out var timings) ||
            timings.ValueKind != JsonValueKind.Object)
            return;

        if (TryGetLong(timings, "cache_n", out var cacheN))
            details.CachedInputTokenCount = cacheN;

        if (TryGetDouble(timings, "prompt_ms", out var promptMs))
            Additional(details)[TimingsKeys.PromptUs] = (long)(promptMs * 1000);

        if (TryGetDouble(timings, "prompt_per_second", out var promptPerSec))
            Additional(details)[TimingsKeys.PromptTokPerSecX100] = (long)(promptPerSec * 100);

        if (TryGetDouble(timings, "predicted_ms", out var predictedMs))
            Additional(details)[TimingsKeys.PredictedUs] = (long)(predictedMs * 1000);

        if (TryGetDouble(timings, "predicted_per_second", out var predictedPerSec))
            Additional(details)[TimingsKeys.PredictedTokPerSecX100] = (long)(predictedPerSec * 100);
    }

    private static AdditionalPropertiesDictionary<long> Additional(UsageDetails d)
        => d.AdditionalCounts ??= [];

    private static bool TryGetLong(JsonElement obj, string name, out long value)
    {
        if (obj.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt64(out value))
        {
            return true;
        }
        value = 0;
        return false;
    }

    private static bool TryGetDouble(JsonElement obj, string name, out double value)
    {
        if (obj.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetDouble(out value))
        {
            return true;
        }
        value = 0;
        return false;
    }
}

/// <summary>
/// vLLM (and any OpenAI-standard prefix-cache backend) reports cache
/// hit count inside <c>usage.prompt_tokens_details.cached_tokens</c>.
/// No per-request timing is exposed by the server — wall-clock
/// measurement around the HTTP call carries that load (see
/// <see cref="OpenAiCompatibleChatClient"/>).
/// </summary>
internal sealed class VllmTimingsExtractor : ITimingsExtractor
{
    public void Extract(JsonElement root, UsageDetails details)
    {
        if (!root.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
            return;

        if (!usage.TryGetProperty("prompt_tokens_details", out var ptd) ||
            ptd.ValueKind != JsonValueKind.Object)
            return;

        if (ptd.TryGetProperty("cached_tokens", out var cached) &&
            cached.ValueKind == JsonValueKind.Number &&
            cached.TryGetInt64(out var value))
        {
            // Last writer wins — if llama.cpp's timings.cache_n already
            // populated this, our value would equal it. Both backends
            // never coexist on the same response.
            details.CachedInputTokenCount = value;
        }
    }
}

/// <summary>
/// Integer-encoded telemetry keys shared between the chat client and
/// the session actor that decodes them. Keep keys in sync.
/// </summary>
internal static class TimingsKeys
{
    public const string PromptUs = "prompt_us";
    public const string PromptTokPerSecX100 = "prompt_tok_per_sec_x100";
    public const string PredictedUs = "predicted_us";
    public const string PredictedTokPerSecX100 = "predicted_tok_per_sec_x100";
}
