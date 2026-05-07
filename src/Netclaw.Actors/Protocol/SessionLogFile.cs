// -----------------------------------------------------------------------
// <copyright file="SessionLogFile.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Shared helper for computing and appending to the canonical per-session log file.
/// The file lives outside the agent-visible session working directory so the LLM
/// cannot inspect its own audit trail with file tools.
///
/// Concurrency contract: callers must serialize writes externally. In production
/// the only writer is <c>SessionLogActor</c>, whose mailbox guarantees a single
/// thread per file path. Tests that exercise this directly must observe the same
/// invariant.
/// </summary>
public static class SessionLogFile
{
    public const string FileName = "session.log";

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

        // Retry on Windows transient SHARING_VIOLATION: AV scanners and the
        // kernel close-completion window can briefly block a fresh open
        // immediately after the prior writer closed. The actor model
        // guarantees a single in-process writer per file, so this is purely
        // an OS-level transient — three attempts with short backoff is
        // sufficient in practice.
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream) { AutoFlush = true };
                writer.WriteLine(line);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(10 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(10 * attempt);
            }
        }
    }
}
