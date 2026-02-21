using Akka.Actor;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Per-session actor managing LLM conversation state.
/// Stub — persistence and turn loop implemented in Task 1.2.
/// </summary>
public sealed class LlmSessionActor : ReceiveActor
{
    public LlmSessionActor(string entityId)
    {
    }
}
