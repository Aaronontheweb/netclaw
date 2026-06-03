// -----------------------------------------------------------------------
// <copyright file="SessionLoggingScope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Shared helper for tagging log lines with the ambient session id.
///
/// The id lives in <see cref="SessionDiagnosticsContext"/> (an AsyncLocal pushed
/// by every session-owned path). The OTLP log exporter has <c>IncludeScopes</c>
/// enabled but cannot read that AsyncLocal directly, so without a logging scope
/// LLM logs reach Seq with no session correlation. Opening a scope keyed on
/// <c>session.id</c> is what surfaces it as a filterable attribute. The scope only
/// adds correlation on the OTLP path (i.e. when telemetry export is enabled); the
/// rolling-file provider ignores scopes and routes <c>session.log</c> independently
/// from the same AsyncLocal.
///
/// Centralized here so every logging decorator (<see cref="LoggingChatClient"/>,
/// <see cref="FailoverChatClient"/>) attaches the id consistently and the
/// <c>session.id</c> key has a single definition.
/// </summary>
internal static class SessionLoggingScope
{
    private const string SessionIdKey = "session.id";

    /// <summary>
    /// Opens a scope tagging subsequent log lines on <paramref name="logger"/> with
    /// the ambient session id, or returns <c>null</c> when no session is in scope
    /// (a no-op <c>using</c>). The single-entry array avoids a per-call dictionary
    /// allocation while still presenting as <c>IEnumerable&lt;KeyValuePair&gt;</c>,
    /// which is what the OTLP exporter projects into log attributes.
    /// </summary>
    public static IDisposable? Begin(ILogger logger)
    {
        var sessionId = SessionDiagnosticsContext.SessionId;
        return sessionId is null
            ? null
            : logger.BeginScope(new[] { new KeyValuePair<string, object>(SessionIdKey, sessionId) });
    }
}
