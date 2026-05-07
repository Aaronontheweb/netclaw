// -----------------------------------------------------------------------
// <copyright file="SessionLogFile.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Shared helper for computing and appending to the canonical per-session log file.
/// The file lives outside the agent-visible session working directory so the LLM
/// cannot inspect its own audit trail with file tools.
/// </summary>
public static class SessionLogFile
{
    public const string FileName = "session.log";

    private static readonly ConcurrentDictionary<string, Lock> FileLocks = new(StringComparer.Ordinal);

    public static string GetLogsDirectory(SessionId sessionId, string sessionLogsBasePath)
    {
        var sanitized = SessionDirectoryHelper.SanitizeSessionId(sessionId);
        return Path.Combine(sessionLogsBasePath, sanitized);
    }

    public static string GetLogPath(SessionId sessionId, string sessionLogsBasePath) =>
        Path.Combine(GetLogsDirectory(sessionId, sessionLogsBasePath), FileName);

    public static void AppendLine(SessionId sessionId, string sessionLogsBasePath, string line)
    {
        var logPath = GetLogPath(sessionId, sessionLogsBasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var appendLock = FileLocks.GetOrAdd(logPath, static _ => new Lock());
        lock (appendLock)
        {
            using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            writer.WriteLine(line);
        }
    }
}
