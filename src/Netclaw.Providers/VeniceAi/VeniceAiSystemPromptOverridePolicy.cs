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
/// on every outbound Venice request.
/// <para>
/// Venice's default behavior is to silently prepend its own "uncensored" system prompt
/// to every chat completion. For Netclaw that is unacceptable: it would corrupt the
/// identity grounding produced by <c>SystemPromptAssembler</c>, eat tokens off the
/// effective context window without Netclaw's compaction math accounting for them,
/// and silently drift behavior any time Venice updates their default prompt.
/// </para>
/// <para>
/// Operator opt-out lives one layer up: <see cref="VeniceAiProviderPlugin"/> does not
/// attach this policy when <see cref="VeniceAiVendorOptions.IncludeVeniceSystemPrompt"/>
/// is <c>true</c>. When attached, the policy is an unconditional clamp — it forces
/// <c>false</c> even if upstream code put a different value in
/// <c>venice_parameters.include_venice_system_prompt</c>.
/// </para>
/// </summary>
internal sealed class VeniceAiSystemPromptOverridePolicy : PipelinePolicy
{
    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        PipelineRequestBodyEditor.EditJsonBody(message, InjectSystemPromptOverride);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        PipelineRequestBodyEditor.EditJsonBody(message, InjectSystemPromptOverride);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void InjectSystemPromptOverride(JsonObject body)
    {
        // Preserve any existing venice_parameters the caller may have set; only
        // clamp include_venice_system_prompt.
        if (body["venice_parameters"] is not JsonObject veniceParams)
        {
            veniceParams = new JsonObject();
            body["venice_parameters"] = veniceParams;
        }

        veniceParams["include_venice_system_prompt"] = false;
    }
}
