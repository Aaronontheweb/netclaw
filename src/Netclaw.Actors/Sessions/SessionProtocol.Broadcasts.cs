// -----------------------------------------------------------------------
// <copyright file="SessionProtocol.Broadcasts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

public static partial class SessionProtocol
{
    // ===== Broadcasts (outbound pub/sub) =====

    /// <summary>
    /// Published via Akka pub/sub after a session completes a turn.
    /// Adapters subscribe to deliver replies through their respective channels.
    /// </summary>
    public sealed record TurnBroadcast : ISessionBroadcast
    {
        public SessionId SessionId { get; init; }

        public SerializableChatMessage AssistantReply { get; init; } = new();

        public long BroadcastAtMs { get; init; }

        public DateTimeOffset BroadcastAt => DateTimeOffset.FromUnixTimeMilliseconds(BroadcastAtMs);
    }

    /// <summary>
    /// Published via Akka pub/sub after a session completes compaction.
    /// </summary>
    public sealed record CompactionBroadcast : ISessionBroadcast
    {
        public SessionId SessionId { get; init; }

        public string Summary { get; init; } = string.Empty;

        public long CompactedAtMs { get; init; }

        public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);
    }
}
