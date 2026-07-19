// -----------------------------------------------------------------------
// <copyright file="McpReconnectGateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpReconnectGateTests
{
    [Fact]
    public async Task ConcurrentReconnects_ReuseFirstSuccessfulReplacement()
    {
        var gate = new McpReconnectGate();
        var reconnectEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReconnect = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hasLiveConnection = false;
        var reconnectCount = 0;
        var observedVersion = gate.CaptureVersion();

        async Task<bool> Reconnect(CancellationToken ct)
        {
            Interlocked.Increment(ref reconnectCount);
            reconnectEntered.TrySetResult();
            await releaseReconnect.Task.WaitAsync(ct);
            hasLiveConnection = true;
            gate.MarkConnectionChanged();
            return true;
        }

        var reconnects = Enumerable.Range(0, 5)
            .Select(_ => gate.ReconnectAsync(
                observedVersion,
                () => hasLiveConnection,
                Reconnect,
                TestContext.Current.CancellationToken))
            .ToArray();

        await reconnectEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        releaseReconnect.TrySetResult();

        var results = await Task.WhenAll(reconnects);

        Assert.All(results, Assert.True);
        Assert.Equal(1, Volatile.Read(ref reconnectCount));
    }

    [Fact]
    public async Task FailedLeader_AllowsWaitingCallerToRetry()
    {
        var gate = new McpReconnectGate();
        var firstReconnectEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstReconnect = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hasLiveConnection = false;
        var reconnectCount = 0;
        var observedVersion = gate.CaptureVersion();

        async Task<bool> Reconnect(CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref reconnectCount);
            if (attempt == 1)
            {
                firstReconnectEntered.TrySetResult();
                await releaseFirstReconnect.Task.WaitAsync(ct);
                return false;
            }

            hasLiveConnection = true;
            gate.MarkConnectionChanged();
            return true;
        }

        var first = gate.ReconnectAsync(
            observedVersion, () => hasLiveConnection, Reconnect,
            TestContext.Current.CancellationToken);
        await firstReconnectEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = gate.ReconnectAsync(
            observedVersion, () => hasLiveConnection, Reconnect,
            TestContext.Current.CancellationToken);

        releaseFirstReconnect.TrySetResult();

        Assert.False(await first);
        Assert.True(await second);
        Assert.Equal(2, Volatile.Read(ref reconnectCount));
    }

    [Fact]
    public async Task TearDownWait_ObservesCancellation()
    {
        var gate = new McpReconnectGate();
        var reconnectEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReconnect = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var reconnect = gate.ReconnectAsync(
            gate.CaptureVersion(),
            static () => false,
            async ct =>
            {
                reconnectEntered.TrySetResult();
                await releaseReconnect.Task.WaitAsync(ct);
                return false;
            },
            TestContext.Current.CancellationToken);
        await reconnectEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        var tearDown = gate.TearDownAsync(static () => Task.CompletedTask, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tearDown);

        releaseReconnect.TrySetResult();
        Assert.False(await reconnect);
    }
}
