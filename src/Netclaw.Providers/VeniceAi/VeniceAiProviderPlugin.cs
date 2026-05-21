// -----------------------------------------------------------------------
// <copyright file="VeniceAiProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using OpenAI;

namespace Netclaw.Providers.VeniceAi;

/// <summary>
/// Daemon-side plugin for Venice.ai. Wraps <see cref="VeniceAiDescriptor"/>
/// and adds SDK client construction with the system-prompt override pipeline policy.
/// <para>
/// Venice's OpenAI-compatible API works with the stock <see cref="OpenAIClient"/>
/// via base-URL swap. The override policy is what makes Venice safe to use as a
/// drop-in: it strips Venice's default-injected system prompt so Netclaw remains
/// the sole author of its system context.
/// </para>
/// </summary>
public sealed class VeniceAiProviderPlugin : ProviderPluginBase<VeniceAiDescriptor>
{
    public VeniceAiProviderPlugin(VeniceAiDescriptor descriptor) : base(descriptor) { }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var apiKey = GetRequiredApiKey(entry, TypeKey);
        var endpoint = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? new Uri(DefaultEndpoint)
            : new Uri(entry.Endpoint);

        var options = new OpenAIClientOptions { Endpoint = endpoint };
        options.AddPolicy(new VeniceAiSystemPromptOverridePolicy(), PipelinePosition.PerCall);
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);

        return client.GetChatClient(model.ModelId).AsIChatClient();
    }
}
