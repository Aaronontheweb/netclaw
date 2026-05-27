// -----------------------------------------------------------------------
// <copyright file="VeniceAiSystemPromptOverridePolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace Netclaw.Providers.VeniceAi;

/// <summary>
/// Pipeline policy that forces <c>venice_parameters.include_venice_system_prompt = false</c>
/// on every outbound Venice request. Venice's default is <c>true</c>, which would silently
/// prepend their own system prompt ahead of Netclaw's <c>SystemPromptAssembler</c> output
/// (corrupting identity grounding and breaking compaction context-budget math). Operators
/// who explicitly want Venice's prefix opt in via
/// <see cref="VeniceAiVendorOptions.IncludeVeniceSystemPrompt"/>; when that's true, this
/// policy is not attached.
/// </summary>
internal sealed class VeniceAiSystemPromptOverridePolicy : PipelinePolicy
{
    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        PipelineRequestBodyEditor.EditJsonBody(message, InjectIncludeVeniceSystemPromptFalse);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        PipelineRequestBodyEditor.EditJsonBody(message, InjectIncludeVeniceSystemPromptFalse);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void InjectIncludeVeniceSystemPromptFalse(JsonObject body)
    {
        if (body["venice_parameters"] is not JsonObject veniceParams)
        {
            veniceParams = new JsonObject();
            body["venice_parameters"] = veniceParams;
        }

        veniceParams["include_venice_system_prompt"] = false;
    }
}
