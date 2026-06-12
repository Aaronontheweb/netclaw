// -----------------------------------------------------------------------
// <copyright file="JobOutputLog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Netclaw.Security;

namespace Netclaw.Actors.Jobs;

/// <summary>
/// Streams a background job's output to its on-disk log as the process produces
/// it, so the log is observable (file_read/grep/check_background_job) while the
/// job runs — a background job is a detached process with no completion
/// expectation, so exit time is too late to make output visible.
/// Lines are secret-redacted at write time (per line — a secret spanning a line
/// boundary would evade the pass; the redactor's patterns are token-shaped, so
/// this is an accepted trade for a live log). Disk is bounded by single-slot
/// rotation: when the current log crosses the threshold it moves to the `.1`
/// slot (replacing any earlier rotation), so a chatty long-running process
/// holds at most ~2x the threshold on disk and the most recent output is
/// always in the current log.
/// </summary>
public sealed class JobOutputLog : IAsyncDisposable
{
    public const long DefaultRotationThresholdBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly long _rotationThresholdBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StreamWriter? _writer;
    private long _bytesWritten;

    public bool Rotated { get; private set; }

    /// <summary>
    /// First write failure, if any. Once a write fails the log stops accepting
    /// lines but callers MUST keep draining the process pipes — a child blocked
    /// on a full pipe never exits. The failure is surfaced on the completion
    /// message so the broken capture is loud, not silent.
    /// </summary>
    public string? WriteFailure { get; private set; }

    public JobOutputLog(string path, long rotationThresholdBytes = DefaultRotationThresholdBytes)
    {
        _path = path;
        _rotationThresholdBytes = rotationThresholdBytes;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Eager-create so the path handed to the agent in the submit ACK is
        // readable from the moment the job starts, not after first output.
        _writer = OpenWriter();
    }

    public string RotatedPath => RotatedPathFor(_path);

    public static string RotatedPathFor(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, $"{stem}.1{ext}");
    }

    public async Task WriteLineAsync(string line, bool isStderr)
    {
        if (WriteFailure is not null)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _writer ??= OpenWriter();
            var redacted = SecretOutputRedactor.Redact(line);
            if (isStderr)
                redacted = "[stderr] " + redacted;

            await _writer.WriteLineAsync(redacted).ConfigureAwait(false);
            _bytesWritten += Encoding.UTF8.GetByteCount(redacted) + Environment.NewLine.Length;

            if (_bytesWritten >= _rotationThresholdBytes)
                Rotate();
        }
        catch (Exception ex)
        {
            WriteFailure = ex.Message;
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // Writer is already broken; the original failure is what gets reported.
            }

            _writer = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _writer?.Dispose();
            _writer = null;
        }
        catch (Exception ex)
        {
            WriteFailure ??= ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Bounded tail read: seeks from the end instead of loading the whole file,
    /// so querying a multi-megabyte log costs O(maxChars). Reads only the
    /// current log — output rotated to the `.1` slot is reachable by path.
    /// </summary>
    public static (string Tail, bool Truncated) ReadTail(string path, int maxChars)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (fs.Length == 0)
            return (string.Empty, false);

        // 4 bytes/char is the UTF-8 worst case; log content is overwhelmingly
        // ASCII so this comfortably over-fetches the requested char count.
        var seekBytes = Math.Min(fs.Length, maxChars * 4L);
        fs.Seek(-seekBytes, SeekOrigin.End);
        var buffer = new byte[seekBytes];
        fs.ReadExactly(buffer);

        var text = Encoding.UTF8.GetString(buffer);
        // A seek landing mid-codepoint decodes to a replacement char at the
        // very start; trim it rather than show mojibake in a tail view.
        text = text.TrimStart('�');
        if (text.Length > maxChars)
            text = text[^maxChars..];

        return (text, fs.Length > seekBytes);
    }

    private StreamWriter OpenWriter() =>
        new(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        File.Move(_path, RotatedPath, overwrite: true);
        Rotated = true;
        _bytesWritten = 0;
        _writer = OpenWriter();
    }
}
