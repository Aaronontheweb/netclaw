// -----------------------------------------------------------------------
// <copyright file="VeniceAiProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using OpenAI;

namespace Netclaw.Providers.VeniceAi;

/// <summary>
/// Daemon-side plugin for Venice.ai. Wraps <see cref="VeniceAiDescriptor"/>
/// with SDK client construction over the OpenAI-compatible endpoint.
/// <para>
/// By default the plugin attaches <see cref="VeniceAiSystemPromptOverridePolicy"/>
/// so Venice's default-injected system prompt cannot prepend to Netclaw's
/// assembled identity context. Operators who explicitly want Venice's prompt
/// set <see cref="VeniceAiVendorOptions.IncludeVeniceSystemPrompt"/> to
/// <c>true</c>; the override policy is then not attached.
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
        var vendorOptions = entry.GetVendorOptions<VeniceAiVendorOptions>() ?? new VeniceAiVendorOptions();

        var options = new OpenAIClientOptions { Endpoint = endpoint };
        if (ShouldAttachSystemPromptOverride(vendorOptions))
            options.AddPolicy(new VeniceAiSystemPromptOverridePolicy(), PipelinePosition.PerCall);

        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);

        return client.GetChatClient(model.ModelId).AsIChatClient();
    }

    // Extracted so the gate logic — the entire point of the IVendorOptions
    // refactor — has a direct unit test. Inlining loses the only behavioral
    // assertion: a regression inverting this returns to silently letting
    // Venice's system prompt prepend to Netclaw's identity context.
    internal static bool ShouldAttachSystemPromptOverride(VeniceAiVendorOptions vendorOptions)
        => !vendorOptions.IncludeVeniceSystemPrompt;
}
