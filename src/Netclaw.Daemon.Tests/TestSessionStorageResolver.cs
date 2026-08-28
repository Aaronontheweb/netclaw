// -----------------------------------------------------------------------
// <copyright file="TestSessionStorageResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Daemon.Tests;

internal sealed class TestSessionStorageResolver(
    NetclawPaths paths,
    string? sessionLogsDirectory = null) : ISessionStorageResolver
{
    public SessionStoragePaths Resolve(SessionId sessionId)
    {
        var sanitized = SessionDirectoryHelper.SanitizeSessionId(sessionId);
        return SessionStoragePaths.CreateLegacy(
            SessionDirectoryHelper.GetSessionDirectory(sessionId, paths.SessionsDirectory),
            sessionLogsDirectory ?? paths.SessionLogsDirectory,
            sanitized);
    }
}
