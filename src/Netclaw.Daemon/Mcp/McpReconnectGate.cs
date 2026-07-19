// -----------------------------------------------------------------------
// <copyright file="McpReconnectGate.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Serializes connection replacement and teardown for one MCP server while
/// allowing overlapping reconnect callers to reuse the first successful
/// replacement instead of tearing it down again.
/// </summary>
internal sealed class McpReconnectGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _connectionVersion;

    public long CaptureVersion() => Interlocked.Read(ref _connectionVersion);

    public void MarkConnectionChanged() => Interlocked.Increment(ref _connectionVersion);

    public async Task<bool> ReconnectAsync(
        long observedVersion,
        Func<bool> hasLiveConnection,
        Func<CancellationToken, Task<bool>> reconnect,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (CaptureVersion() != observedVersion && hasLiveConnection())
                return true;

            return await reconnect(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TearDownAsync(Func<Task> tearDown, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await tearDown();
            MarkConnectionChanged();
        }
        finally
        {
            _gate.Release();
        }
    }
}
