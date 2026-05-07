// -----------------------------------------------------------------------
// <copyright file="SessionDiagnosticsContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Ambient session context used by diagnostics sinks that need to distinguish
/// daemon-global logs from session-owned logs.
/// </summary>
public static class SessionDiagnosticsContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? SessionId
    {
        get => Current.Value;
        set => Current.Value = NormalizeSessionId(value);
    }

    public static IDisposable Push(string? sessionId)
    {
        var prior = Current.Value;
        Current.Value = NormalizeSessionId(sessionId);
        return new RestoreScope(prior);
    }

    /// <summary>
    /// Trims whitespace and collapses any nested sub-agent suffix back to the
    /// owning session id. Sub-agents run as ephemeral children of a parent
    /// session and reuse its <c>session.log</c> for the audit trail; treating
    /// their composite ids (<c>{parentId}/subagent/{agentName}</c>) as
    /// distinct sessions would scatter sub-agent diagnostics into per-agent
    /// files that operators do not monitor. The split here is the only
    /// place that decision is made — keep it in sync with how sub-agent
    /// ids are constructed inside the sub-agent spawner.
    /// </summary>
    public static string? NormalizeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var value = sessionId.Trim();
        var subAgentMarker = value.IndexOf("/subagent/", StringComparison.Ordinal);
        if (subAgentMarker > 0)
            value = value[..subAgentMarker];

        return value;
    }

    private sealed class RestoreScope(string? prior) : IDisposable
    {
        private string? _prior = prior;

        public void Dispose()
        {
            Current.Value = _prior;
            _prior = null;
        }
    }
}
