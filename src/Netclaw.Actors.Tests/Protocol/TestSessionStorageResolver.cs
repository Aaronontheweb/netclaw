// -----------------------------------------------------------------------
// <copyright file="TestSessionStorageResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Protocol;

internal sealed class TestSessionStorageResolver : ISessionStorageResolver
{
    private static readonly NetclawPaths SharedPaths = new(Path.Combine(
        Path.GetTempPath(),
        "netclaw-test-session-storage"));

    internal static TestSessionStorageResolver Instance { get; } = new(SharedPaths);

    private readonly NetclawPaths _paths;

    internal TestSessionStorageResolver(NetclawPaths paths)
    {
        _paths = paths;
    }

    public SessionStoragePaths Resolve(SessionId sessionId)
    {
        var sanitized = SessionDirectoryHelper.SanitizeSessionId(sessionId);
        return SessionStoragePaths.CreateLegacy(
            SessionDirectoryHelper.GetSessionDirectory(sessionId, _paths.SessionsDirectory),
            _paths.SessionLogsDirectory,
            sanitized);
    }
}
