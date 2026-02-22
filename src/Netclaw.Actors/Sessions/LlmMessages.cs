using Microsoft.Extensions.AI;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Internal message sent back to the session actor when the async LLM call completes.
/// </summary>
internal sealed record LlmResponseReceived
{
    public required ChatResponse Response { get; init; }
}

/// <summary>
/// Internal message sent back to the session actor when the async LLM call fails.
/// </summary>
internal sealed record LlmCallFailed
{
    public required Exception Cause { get; init; }
}

/// <summary>
/// Internal message sent back to the session actor when tool execution completes.
/// Contains the tool results to feed back into the next LLM call.
/// </summary>
internal sealed record ToolExecutionCompleted
{
    public required List<Protocol.SerializableChatMessage> ToolResults { get; init; }
}

/// <summary>
/// Internal message sent back when tool execution fails.
/// </summary>
internal sealed record ToolExecutionFailed
{
    public required Exception Cause { get; init; }
}
