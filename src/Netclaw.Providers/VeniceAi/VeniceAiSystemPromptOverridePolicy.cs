// -----------------------------------------------------------------------
// <copyright file="VeniceAiSystemPromptOverridePolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
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
/// Hardcoded to <c>false</c> with no operator override on purpose — see issue #1136
/// for the design of a future <c>IVendorOptions</c> surface that would expose this
/// (and other Venice knobs) to operators who explicitly want them.
/// </para>
/// </summary>
internal sealed class VeniceAiSystemPromptOverridePolicy : PipelinePolicy
{
    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        InjectSystemPromptOverride(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        InjectSystemPromptOverride(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void InjectSystemPromptOverride(PipelineMessage message)
    {
        var request = message.Request;
        if (request.Content is null)
            return;

        using var stream = new MemoryStream();
        request.Content.WriteTo(stream, default);
        var bytes = stream.ToArray();

        var node = JsonNode.Parse(bytes);
        if (node is not JsonObject obj)
            return;

        // Preserve any existing venice_parameters the caller may have set; only
        // force include_venice_system_prompt. This keeps the policy compatible
        // with future per-call vendor options without re-engineering it.
        if (obj["venice_parameters"] is not JsonObject veniceParams)
        {
            veniceParams = new JsonObject();
            obj["venice_parameters"] = veniceParams;
        }

        veniceParams["include_venice_system_prompt"] = false;

        var modified = JsonSerializer.SerializeToUtf8Bytes(obj);
        request.Content = BinaryContent.Create(BinaryData.FromBytes(modified));
    }
}
