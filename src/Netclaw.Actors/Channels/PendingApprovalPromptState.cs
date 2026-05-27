// -----------------------------------------------------------------------
// <copyright file="PendingApprovalPromptState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Journaled by a channel binding actor after it successfully posts an approval
/// prompt and captures the transport-specific locator needed to redraw that
/// prompt after a later cold spawn.
/// </summary>
public sealed record PendingApprovalPromptTracked : INetclawSerializableMessage
{
    /// <summary>
    /// Hard cap on persisted display text. Per-channel render-time truncation
    /// (Slack 2500, Discord 1700, Mattermost 12000) still applies on top — this
    /// is only the journal-side ceiling to keep snapshots from ballooning when a
    /// model emits a multi-KB shell command body. Large enough to fit every
    /// supported transport's cap with margin.
    /// </summary>
    public const int MaxPersistedDisplayTextChars = 16_384;

    public string CallId { get; init; } = string.Empty;

    public string? RequesterSenderId { get; init; }

    public PrincipalClassification? RequesterPrincipal { get; init; }

    public IReadOnlyList<string> OptionKeys { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Opaque transport-specific prompt locator: Slack message ts, Discord
    /// message id, or Mattermost post id.
    /// </summary>
    public string PromptId { get; init; } = string.Empty;

    /// <summary>
    /// Tool name from the original <c>ToolInteractionRequest</c>. Null on journal
    /// entries written before this field was added — the cold-spawn redraw then
    /// falls back to the generic resolution banner.
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// Display text from the original <c>ToolInteractionRequest</c>, truncated
    /// to <see cref="MaxPersistedDisplayTextChars"/> before persistence. Null on
    /// journal entries written before this field was added.
    /// </summary>
    public string? DisplayText { get; init; }
}

/// <summary>
/// Journaled by a channel binding actor when a previously tracked approval
/// prompt is no longer pending locally.
/// </summary>
public sealed record PendingApprovalPromptCleared : INetclawSerializableMessage
{
    public string CallId { get; init; } = string.Empty;
}
