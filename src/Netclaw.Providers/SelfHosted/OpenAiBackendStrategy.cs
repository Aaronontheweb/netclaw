// -----------------------------------------------------------------------
// <copyright file="OpenAiBackendStrategy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Wraps the raw probe results an OpenAI-compatible capability resolver
/// shares across strategy implementations. <see cref="PropsJson"/> is null
/// when the backend has no <c>/props</c> endpoint (e.g. vLLM returns 404).
/// </summary>
internal sealed record BackendProbe(string ModelId, string ModelsJson, string? PropsJson);

/// <summary>
/// Backend-specific parser for an OpenAI-compatible endpoint. The
/// resolver enumerates strategies in priority order and uses the first
/// whose <see cref="Matches"/> predicate is true.
/// </summary>
internal interface IOpenAiBackendStrategy
{
    /// <summary>Diagnostic name written to logs when a backend is detected.</summary>
    string Name { get; }

    bool Matches(BackendProbe probe);

    ResolvedModelCapabilities? Parse(BackendProbe probe);
}

/// <summary>
/// vLLM-specific strategy. Recognizes vLLM via either <c>owned_by: "vllm"</c>
/// on a <c>/v1/models</c> entry or the presence of a top-level
/// <c>max_model_len</c> on the model entry combined with <c>/props</c>
/// returning non-200. Parses <c>max_model_len</c> as the context window
/// and leaves modalities null so downstream resolvers (HuggingFace) can
/// fill them — vLLM exposes no modality field anywhere.
/// </summary>
internal sealed class VllmBackendStrategy : IOpenAiBackendStrategy
{
    public string Name => "vllm";

    public bool Matches(BackendProbe probe)
    {
        if (!TryFindModelEntry(probe, out var model))
            return false;

        if (model.TryGetProperty("owned_by", out var ownedBy) &&
            ownedBy.ValueKind == JsonValueKind.String &&
            string.Equals(ownedBy.GetString(), "vllm", StringComparison.OrdinalIgnoreCase))
            return true;

        // /props 404 + max_model_len present is also a strong vLLM signal.
        return probe.PropsJson is null &&
               model.TryGetProperty("max_model_len", out var mml) &&
               mml.ValueKind == JsonValueKind.Number;
    }

    public ResolvedModelCapabilities? Parse(BackendProbe probe)
    {
        if (!TryFindModelEntry(probe, out var model))
            return null;

        int? contextWindow = null;
        if (model.TryGetProperty("max_model_len", out var mml) &&
            mml.ValueKind == JsonValueKind.Number)
        {
            contextWindow = mml.GetInt32();
        }

        // vLLM exposes no modality information; null fields signal that
        // downstream resolvers in the chain (HuggingFace) should fill in.
        return new ResolvedModelCapabilities(probe.ModelId, null, null, contextWindow);
    }

    private static bool TryFindModelEntry(BackendProbe probe, out JsonElement entry)
    {
        entry = default;
        using var doc = JsonDocument.Parse(probe.ModelsJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var model in data.EnumerateArray())
        {
            if (model.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), probe.ModelId, StringComparison.OrdinalIgnoreCase))
            {
                entry = model.Clone();
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// llama.cpp-specific strategy. Recognizes llama.cpp via either a
/// successful <c>/props</c> response or a <c>meta.n_ctx_train</c> field
/// on a <c>/v1/models</c> entry. Prefers <c>/props.default_generation_settings.params.n_ctx</c>
/// for the runtime-effective context window, and reads
/// <c>/props.modalities.vision</c> for input modality.
/// </summary>
internal sealed class LlamaCppBackendStrategy : IOpenAiBackendStrategy
{
    public string Name => "llama.cpp";

    public bool Matches(BackendProbe probe)
    {
        if (probe.PropsJson is not null)
            return true;

        if (!TryFindModelEntry(probe, out var model))
            return false;

        return model.TryGetProperty("meta", out var meta) &&
               meta.ValueKind == JsonValueKind.Object &&
               meta.TryGetProperty("n_ctx_train", out var ctx) &&
               ctx.ValueKind == JsonValueKind.Number;
    }

    public ResolvedModelCapabilities? Parse(BackendProbe probe)
    {
        // Start with what /v1/models tells us about context window.
        int? contextWindow = null;
        if (TryFindModelEntry(probe, out var model) &&
            model.TryGetProperty("meta", out var meta) &&
            meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("n_ctx_train", out var ctx) &&
            ctx.ValueKind == JsonValueKind.Number)
        {
            contextWindow = ctx.GetInt32();
        }

        // /props overrides — runtime-effective n_ctx and vision flag.
        var inputModalities = ModelModality.Text;
        if (probe.PropsJson is not null)
        {
            using var propsDoc = JsonDocument.Parse(probe.PropsJson);
            var root = propsDoc.RootElement;

            if (root.TryGetProperty("default_generation_settings", out var dgs) &&
                dgs.ValueKind == JsonValueKind.Object &&
                dgs.TryGetProperty("params", out var parameters) &&
                parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("n_ctx", out var nCtx) &&
                nCtx.ValueKind == JsonValueKind.Number)
            {
                contextWindow = nCtx.GetInt32();
            }

            if (root.TryGetProperty("modalities", out var modalities) &&
                modalities.ValueKind == JsonValueKind.Object &&
                modalities.TryGetProperty("vision", out var vision) &&
                vision.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                vision.GetBoolean())
            {
                inputModalities |= ModelModality.Image;
            }
        }

        return new ResolvedModelCapabilities(
            probe.ModelId, inputModalities, ModelModality.Text, contextWindow);
    }

    private static bool TryFindModelEntry(BackendProbe probe, out JsonElement entry)
    {
        entry = default;
        using var doc = JsonDocument.Parse(probe.ModelsJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var model in data.EnumerateArray())
        {
            if (model.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), probe.ModelId, StringComparison.OrdinalIgnoreCase))
            {
                entry = model.Clone();
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Last-resort fallback when neither vLLM nor llama.cpp signals are
/// present. Returns the model id with null fields so downstream
/// resolvers (OpenRouter oracle, HuggingFace) have the chance to fill
/// in. Always matches.
/// </summary>
internal sealed class GenericOpenAiBackendStrategy : IOpenAiBackendStrategy
{
    public string Name => "generic-openai";

    public bool Matches(BackendProbe probe) => true;

    public ResolvedModelCapabilities? Parse(BackendProbe probe)
        => new(probe.ModelId, null, null, null);
}
