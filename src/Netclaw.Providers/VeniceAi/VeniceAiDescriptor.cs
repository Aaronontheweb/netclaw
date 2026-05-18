// -----------------------------------------------------------------------
// <copyright file="VeniceDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using Netclaw.Configuration;

namespace Netclaw.Providers.VeniceAi;

/// <summary>
/// Provider descriptor for Venice.ai
/// </summary>
public sealed class VeniceAiDescriptor : IProviderDescriptor
{
    private readonly HttpClient _httpClient;

    public VeniceAiDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "veniceai";
    public string DisplayName => "Venice.ai";
    public string DefaultEndpoint => "https://api.venice.ai";
    public string ModelListingPath => "/v1/models";

    public IProviderAuth Auth { get; } = new ApiKeyAuth
    {
        GuidanceUrl = new Uri("https://venice.ai/settings/api"),
    };

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var apiKey = entry.ApiKey?.Value;
        if (string.IsNullOrWhiteSpace(apiKey))
            return Task.FromResult(new ProviderProbeResult(false,
                "API key is required for Venice. Get one at https://venice.ai/settings/api", []));

        return ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey),
            ProbeHelpers.ParseOpenAiStyleModels,
            ct);
    }
}
