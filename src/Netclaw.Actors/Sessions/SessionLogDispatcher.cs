// -----------------------------------------------------------------------
// <copyright file="SessionLogDispatcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Root actor that owns one <see cref="SessionLogActor"/> child per sanitized
/// session id. Routing is by message field (<see cref="IWithSessionId.SessionId"/>
/// or the explicit <see cref="SessionLogDiagnostic.SessionId"/>) so callers do
/// not need to track per-session actor refs.
///
/// Children are created lazily on first message and stopped by their own
/// idle <see cref="ReceiveTimeout"/>. Akka's child registry replaces the
/// hand-rolled lock dictionary that used to serialize concurrent writers
/// in <c>SessionLogFile</c>.
/// </summary>
public sealed class SessionLogDispatcher : ReceiveActor
{
    private readonly string _sessionLogsBasePath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _childIdleTimeout;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    public static Props CreateProps(string sessionLogsBasePath, TimeProvider timeProvider, TimeSpan? childIdleTimeout = null) =>
        Props.Create(() => new SessionLogDispatcher(sessionLogsBasePath, timeProvider, childIdleTimeout ?? TimeSpan.FromMinutes(10)));

    public SessionLogDispatcher(string sessionLogsBasePath, TimeProvider timeProvider, TimeSpan childIdleTimeout)
    {
        _sessionLogsBasePath = sessionLogsBasePath;
        _timeProvider = timeProvider;
        _childIdleTimeout = childIdleTimeout;

        Receive<SessionLogDiagnostic>(msg => Forward(msg.SessionId, msg));
        Receive<SendUserMessage>(msg => Forward(msg.SessionId, msg));
        Receive<SessionOutput>(msg => Forward(msg.SessionId, msg));
    }

    private void Forward(SessionId sessionId, object message)
    {
        var child = ResolveOrCreateChild(sessionId);
        child.Forward(message);
    }

    private IActorRef ResolveOrCreateChild(SessionId sessionId)
    {
        var name = SessionDirectoryHelper.SanitizeSessionId(sessionId);
        var existing = Context.Child(name);
        if (!existing.IsNobody())
            return existing;

        return Context.ActorOf(
            SessionLogActor.CreateProps(sessionId, _sessionLogsBasePath, _timeProvider, _childIdleTimeout),
            name);
    }
}
