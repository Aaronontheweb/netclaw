// -----------------------------------------------------------------------
// <copyright file="ToolApprovalActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal sealed class ToolApprovalActor : ReceiveActor
{
    private readonly ToolApprovalStore? _persistentStore;
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _sessionApprovals = new(StringComparer.Ordinal);

    public ToolApprovalActor(ToolApprovalStore? persistentStore = null)
    {
        _persistentStore = persistentStore;

        Receive<GetUnapprovedPatterns>(msg =>
        {
            var unapproved = new List<string>(msg.Patterns.Count);

            foreach (var pattern in msg.Patterns)
            {
                if (!IsApproved(msg.SessionId, msg.Audience, msg.ToolName, pattern, msg.Cwd))
                    unapproved.Add(pattern);
            }

            Sender.Tell(new UnapprovedPatternsResponse(unapproved));
        });

        Receive<RecordToolApproval>(msg =>
        {
            foreach (var pattern in msg.Patterns)
            {
                AddSessionApproval(msg.SessionId, msg.Audience, msg.ToolName, pattern);

                if (msg.Persistent)
                {
                    // Until section 7's prompt redesign supplies an explicit
                    // scope from the user's button click, the runtime persists
                    // every "Always"-style grant as a global wildcard
                    // (verb, null). Section 7 plumbs the per-button scope and
                    // produces folder-scoped (verb, cwd) entries for the
                    // "Always here" path while keeping (verb, null) for
                    // "Always anywhere".
                    _persistentStore?.AddApproval(
                        msg.Audience,
                        msg.ToolName.Value,
                        new ApprovalEntry { Verb = pattern, Directory = null });
                }
            }

            Sender.Tell(ToolApprovalRecorded.Instance);
        });
    }

    public static Props CreateProps(ToolApprovalStore? persistentStore = null)
        => Props.Create(() => new ToolApprovalActor(persistentStore));

    private bool IsApproved(SessionId? sessionId, TrustAudience audience, ToolName toolName, string candidateVerb, string? cwd)
    {
        if (sessionId.HasValue && IsSessionApproved(sessionId.Value, audience, toolName, candidateVerb))
            return true;

        if (_persistentStore is null)
            return false;

        var approved = _persistentStore.GetApprovedEntries(audience, toolName.Value);
        return MatchesPersistedEntry(toolName, candidateVerb, cwd, approved);
    }

    private bool IsSessionApproved(SessionId sessionId, TrustAudience audience, ToolName toolName, string candidateVerb)
    {
        // Walk up the scope chain: sub-agent scopes inherit parent session approvals.
        // Scope format: "{parentSessionId}/subagent/{name}/{runId}" — parent is the prefix before "/subagent/".
        var scopeId = sessionId.Value;
        while (true)
        {
            var sessionKey = BuildSessionKey((SessionId)scopeId, audience);
            if (_sessionApprovals.TryGetValue(sessionKey, out var toolMap)
                && toolMap.TryGetValue(toolName.Value, out var verbs)
                && verbs.Contains(candidateVerb))
            {
                return true;
            }

            var subagentMarker = scopeId.IndexOf("/subagent/", StringComparison.Ordinal);
            if (subagentMarker <= 0)
                break;

            scopeId = scopeId[..subagentMarker];
        }

        return false;
    }

    private void AddSessionApproval(SessionId sessionId, TrustAudience audience, ToolName toolName, string candidateVerb)
    {
        var sessionKey = BuildSessionKey(sessionId, audience);
        if (!_sessionApprovals.TryGetValue(sessionKey, out var toolMap))
        {
            toolMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            _sessionApprovals[sessionKey] = toolMap;
        }

        if (!toolMap.TryGetValue(toolName.Value, out var verbs))
        {
            // Session approvals use the same platform-correct comparer as the
            // persistent store (Ordinal on POSIX, OrdinalIgnoreCase on Windows)
            // so a grant for `git` cannot be redeemed by a planted `Git`
            // earlier in $PATH on case-sensitive filesystems.
            verbs = new HashSet<string>(ToolApprovalEntryComparer.Comparer);
            toolMap[toolName.Value] = verbs;
        }

        verbs.Add(candidateVerb);
    }

    private static bool MatchesPersistedEntry(ToolName toolName, string candidateVerb, string? cwd, IReadOnlyList<ApprovalEntry> approved)
        => string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal)
            ? ApprovalPatternMatching.MatchesShellApproval(candidateVerb, cwd, approved)
            : ApprovalPatternMatching.MatchesAny(candidateVerb, approved);

    private static string BuildSessionKey(SessionId sessionId, TrustAudience audience)
        => $"{sessionId.Value}|{audience.ToWireValue()}";
}

internal sealed record ToolApprovalRecorded
{
    public static ToolApprovalRecorded Instance { get; } = new();
}
