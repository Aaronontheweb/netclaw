// -----------------------------------------------------------------------
// <copyright file="CurrentTurnScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Owns the transient "what turn is active and where did it come from" state:
/// the inbound source, the derived turn/trust context, the turn's recalled
/// memories, and the diagnostic correlation identity (turn/message/channel).
/// Populated at turn start and re-bound on approval re-drive; the actor reads
/// these to build persisted event records, authorize tool exposure, and enrich
/// turn-scoped logs.
///
/// The correlation identity (<see cref="TurnId"/>/<see cref="MessageId"/>/
/// <see cref="ChannelType"/>) is mutated only through <see cref="Bind(MessageSource?)"/>
/// and <see cref="Bind(TurnContext)"/> so the three stay in lockstep. The
/// remaining fields are overwritten each turn; only <see cref="TurnContext"/>
/// is explicitly cleared at a turn boundary (the actor nulls it directly).
/// </summary>
internal sealed class CurrentTurnScope
{
    /// <summary>Provenance of the active turn (channel, sender, reminder/job id).</summary>
    public MessageSource? Source { get; set; }

    /// <summary>Trust/boundary/audience context derived from <see cref="Source"/>.</summary>
    public TurnContext? TurnContext { get; set; }

    /// <summary>Effective trust context used to authorize approvals and tool exposure.</summary>
    public EffectiveTrustContext? TrustContext { get; set; }

    /// <summary>Memories recalled for this turn, reused across the tool loop.</summary>
    public AutomaticRecallResult? Recall { get; set; }

    /// <summary>Correlation turn id for telemetry/logging (ephemeral).</summary>
    public Protocol.TurnId? TurnId { get; private set; }

    /// <summary>Inbound message id for crash-context breadcrumbs (ephemeral).</summary>
    public string? MessageId { get; private set; }

    /// <summary>Channel type of the active turn (ephemeral).</summary>
    public Channels.ChannelType? ChannelType { get; private set; }

    /// <summary>
    /// Establishes the diagnostic correlation identity for a turn from its
    /// inbound source, generating a turn id when the source carries none.
    /// </summary>
    public void Bind(MessageSource? source)
    {
        MessageId = source?.MessageId;
        TurnId = source?.TurnId ?? new Protocol.TurnId(MessageId ?? IdGen.ShortId());
        ChannelType = source?.ChannelType;
    }

    /// <summary>
    /// Re-binds correlation identity from a recovered or parked turn context.
    /// No inbound message id is available on this path.
    /// </summary>
    public void Bind(TurnContext context)
    {
        MessageId = null;
        TurnId = context.TurnId;
        ChannelType = context.ChannelType;
    }
}
