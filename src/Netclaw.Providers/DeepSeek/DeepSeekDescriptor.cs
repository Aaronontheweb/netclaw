// -----------------------------------------------------------------------
// <copyright file="DeepSeekDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using Netclaw.Configuration;

namespace Netclaw.Providers.DeepSeek;

/// <summary>
/// Provider descriptor for DeepSeek's hosted OpenAI-compatible API.
/// </summary>
public sealed class DeepSeekDescriptor(HttpClient httpClient) : IProviderDescriptor
{
    private const int CurrentContextWindow = 1_000_000;

    public string TypeKey => "deepseek";

    public string DisplayName => "DeepSeek";

    public string DefaultEndpoint => "https://api.deepseek.com/v1";

    public string ModelListingPath => "/models";

    public IProviderAuth Auth { get; } = new ApiKeyAuth
    {
        GuidanceUrl = new Uri("https://platform.deepseek.com/api_keys"),
    };

    public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult(new ProviderProbeResult(
                false,
                "API key is required for DeepSeek. Get one at https://platform.deepseek.com/api_keys",
                []));
        }

        return ProbeHelpers.ExecuteProbeAsync(
            httpClient,
            TypeKey,
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey),
            ParseModels,
            ct);
    }

    internal static ProviderProbeResult ParseModels(string json)
    {
        var parsed = ProbeHelpers.ParseOpenAiStyleModels(json);
        var models = parsed.Models
            .Select(model => IsCurrentModel(model.ModelId.Value)
                ? model with
                {
                    ContextWindowTokens = CurrentContextWindow,
                    InputModalities = ModelModality.Text,
                    OutputModalities = ModelModality.Text,
                }
                : model)
            .ToArray();

        return parsed with { Models = models };
    }

    private static bool IsCurrentModel(string modelId) =>
        modelId is "deepseek-v4-flash" or "deepseek-v4-pro";
}
