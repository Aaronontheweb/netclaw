// -----------------------------------------------------------------------
// <copyright file="SessionSnapshot.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Snapshot of session state for fast recovery. Persisted after compaction
/// and periodically based on <see cref="Sessions.SessionConfig.SnapshotInterval"/>.
/// </summary>
public sealed record SessionSnapshot : INetclawSerializableMessage
{
    public sealed record AdoptedContextSnapshotRecord
    {
        public sealed record AdoptedContextSnapshotMessage
        {
            public string MessageId { get; init; } = string.Empty;

            public SenderId SenderId { get; init; } = new(string.Empty);

            public long TimestampMs { get; init; }

            public string AuthorityAtInclusion { get; init; } = string.Empty;
        }

        public string AuthorizedMessageId { get; init; } = string.Empty;

        public SenderId? AuthorizerSenderId { get; init; }

        public string? LowerBound { get; init; }

        public string? UpperBound { get; init; }

        public string Projection { get; init; } = string.Empty;

        public bool HasAdoptedContext { get; init; }

        public bool HasThirdPartyAdoptedContext { get; init; }

        public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = Array.Empty<string>();

        public bool ProjectionPersisted { get; init; }

        public IReadOnlyList<AdoptedContextSnapshotMessage> Messages { get; init; } =
            Array.Empty<AdoptedContextSnapshotMessage>();
    }

    /// <summary>
    /// A single <c>(verb, directory)</c> approval candidate clause carried by a
    /// persisted <see cref="PendingToolInteractionRecord"/>. Framework-owned
    /// persistence mirror of <see cref="Netclaw.Security.ApprovalCandidate"/>.
    /// </summary>
    public sealed record ApprovalCandidateRecord
    {
        public string Verb { get; init; } = string.Empty;

        public string? Directory { get; init; }
    }

    /// <summary>
    /// Persisted form of an in-flight tool-approval interaction. A turn parks
    /// when a tool call needs human approval; persisting the pending
    /// interaction lets a recovered session re-drive the parked tool batch
    /// once a Slack/Discord approval click arrives after passivation.
    /// </summary>
    public sealed record PendingToolInteractionRecord
    {
        /// <summary>Tool call id of the parked invocation (the runtime dictionary key).</summary>
        public string CallId { get; init; } = string.Empty;

        public string ToolName { get; init; } = string.Empty;

        public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> CandidateVerbs { get; init; } = Array.Empty<string>();

        public TrustAudience Audience { get; init; }

        public SenderId? RequesterSenderId { get; init; }

        public PrincipalClassification? RequesterPrincipal { get; init; }

        public string? Cwd { get; init; }

        public IReadOnlyList<ApprovalCandidateRecord> Candidates { get; init; } =
            Array.Empty<ApprovalCandidateRecord>();
    }

    public IReadOnlyList<SerializableChatMessage> History { get; init; } =
        Array.Empty<SerializableChatMessage>();

    public int TurnCount { get; init; }

    public string? Title { get; init; }

    /// <summary>
    /// Persisted so a recovered session can handle late-arriving
    /// <see cref="DeliveryFailed"/> feedback after passivation.
    /// Null when no turn is eligible (initial state or retries exhausted).
    /// </summary>
    public TurnNumber? EligibleDeliveryTurnNumber { get; init; }

    /// <summary>
    /// Durable working-context state (recent files). Null when the session
    /// has never set a non-empty context — <see cref="Sessions.SessionState.FromSnapshot"/>
    /// defaults to <see cref="WorkingContext.Empty"/> in that case.
    /// </summary>
    public WorkingContext? WorkingContext { get; init; }

    /// <summary>
    /// Background jobs this session is waiting on. Persisted because jobs
    /// are long-lived and must survive recovery.
    /// </summary>
    public IReadOnlyList<ActiveJobInfo> ActiveBackgroundJobs { get; init; } =
        Array.Empty<ActiveJobInfo>();

    public IReadOnlyList<AdoptedContextSnapshotRecord> AdoptedContextRecords { get; init; } =
        Array.Empty<AdoptedContextSnapshotRecord>();

    /// <summary>
    /// In-flight tool-approval interactions awaiting a user decision. Persisted
    /// so a recovered session can re-drive a parked tool batch after a
    /// Slack/Discord approval click arrives post-passivation. Empty when no
    /// approval is pending — which is the case for any snapshot written before
    /// this field existed (proto3 default).
    /// </summary>
    public IReadOnlyList<PendingToolInteractionRecord> PendingToolInteractions { get; init; } =
        Array.Empty<PendingToolInteractionRecord>();
}
