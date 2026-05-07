// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Daemon-side plugin for OpenAI-compatible endpoints such as Lemonade or vLLM.
/// </summary>
public sealed class OpenAiCompatibleProviderPlugin : ProviderPluginBase<OpenAiCompatibleDescriptor>
{
    private readonly ILogger<OpenAiCompatibleChatClient> _logger;

    public OpenAiCompatibleProviderPlugin(
        OpenAiCompatibleDescriptor descriptor,
        ILogger<OpenAiCompatibleChatClient> logger) : base(descriptor)
    {
        _logger = logger;
    }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            entry.Endpoint ?? DefaultEndpoint,
            entry.ApiKey?.Value);

        return new OpenAiCompatibleChatClient(
            CreateLlmHttpClient(endpoint.BaseUri),
            endpoint,
            model.ModelId,
            _logger);
    }
}
