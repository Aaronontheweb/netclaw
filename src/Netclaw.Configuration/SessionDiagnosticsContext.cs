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
