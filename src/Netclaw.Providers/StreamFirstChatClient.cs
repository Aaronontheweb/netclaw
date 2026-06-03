// -----------------------------------------------------------------------
// <copyright file="StreamFirstChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Providers;

/// <summary>
/// Serves non-streaming <see cref="IChatClient.GetResponseAsync"/> by consuming the
/// underlying streaming endpoint and aggregating the updates, so streaming is the
/// single request mode the daemon ever issues at the provider boundary.
///
/// The interactive session loop already streams; only auxiliary calls (title
/// generation, memory distillation, compaction, curation) use the non-streaming
/// path. Some providers are streaming-only — notably the OpenAI Codex backend, which
/// rejects non-streaming Responses requests with
/// <c>400 {"detail":"Stream must be set to true"}</c>. Applying this universally at
/// the provider boundary lets every provider serve a complete response without a
/// per-provider/per-model capability model, since streaming is the near-universal
/// capability among the providers Netclaw targets (and any environment that breaks
/// streaming would already break the main session loop, which streams too).
///
/// Cost: <see cref="GetResponseAsync"/> consumes the whole stream before returning —
/// the same total latency as a native non-streaming call, since the caller waits for
/// completion either way. Microsoft.Extensions.AI's <c>ToChatResponseAsync</c>
/// coalesces text, usage, and tool-call deltas into the aggregated response.
/// Streaming calls pass straight through unchanged.
/// </summary>
public sealed class StreamFirstChatClient : DelegatingChatClient
{
    public StreamFirstChatClient(IChatClient innerClient) : base(innerClient) { }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken);
}
