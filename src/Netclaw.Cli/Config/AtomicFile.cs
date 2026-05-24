// -----------------------------------------------------------------------
// <copyright file="AtomicFile.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Config;

/// <summary>
/// Atomic file write helpers. Writes go to a sibling temp file first and
/// then replace the target with <see cref="File.Move(string, string, bool)"/>,
/// so a process kill or power loss mid-write cannot leave a torn file.
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="targetPath"/>
    /// atomically. The temp file is named <c>&lt;target&gt;.tmp.&lt;pid&gt;.&lt;rand&gt;</c>
    /// so concurrent saves do not collide.
    /// </summary>
    public static void WriteAllText(string targetPath, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(contents);

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = $"{targetPath}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(tempPath, contents);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup; the original target is untouched if Move
            // never ran (atomic semantics on the same volume).
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* swallow cleanup failure */ }
            throw;
        }
    }
}
