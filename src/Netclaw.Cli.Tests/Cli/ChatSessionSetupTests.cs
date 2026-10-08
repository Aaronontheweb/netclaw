// -----------------------------------------------------------------------
// <copyright file="ChatSessionSetupTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// The chat sets its session up from three places that can overlap: the connect loop, the
/// Connected connection event and a submit that finds the chat not ready. Each test holds the
/// first session set-up open and lets the others pile up behind it, so the overlap is certain
/// and no test sleeps or polls.
/// </summary>
public sealed class ChatSessionSetupTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeDaemonHubTransport _transport = new();
    private readonly ConcurrentQueue<string> _sent = new();
    private readonly SemaphoreSlim _sentSignal = new(0);
    private readonly TaskCompletionSource _firstSetupReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstSetup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _gated;

    public ChatSessionSetupTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        // The first EnsureSession RPC is held until the test releases it.
        _transport.EnsureSessionGate = async _ =>
        {
            if (Interlocked.Exchange(ref _gated, 1) == 0)
            {
                _firstSetupReached.TrySetResult();
                await _releaseFirstSetup.Task;
            }
        };
        _transport.VoidInvokeHook = (method, args, _) =>
        {
            if (method == "SendMessage" && args.Length > 1 && args[1] is string text)
            {
                _sent.Enqueue(text);
                _sentSignal.Release();
            }

            return Task.CompletedTask;
        };
    }

    public void Dispose()
    {
        _releaseFirstSetup.TrySetResult();
        _sentSignal.Dispose();
        _dir.Dispose();
    }

    private async Task<(ChatViewModel Chat, DaemonClient Client)> StartChatAsync(ChatNavigationState? navigation = null)
    {
        var client = new DaemonClient(
            "http://localhost", _transport, reconnectDelays: [TimeSpan.Zero], rpcTimeout: TimeSpan.FromSeconds(30));
        var chat = new ChatViewModel(
            client,
            TimeProvider.System,
            new ModelCapabilities { ModelId = "test-model" },
            navigation ?? new ChatNavigationState(),
            _paths);
        chat.OnActivated();
        await _firstSetupReached.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        return (chat, client);
    }

    private async Task WaitForSendsAsync(int count)
    {
        for (var i = 0; i < count; i++)
            Assert.True(await _sentSignal.WaitAsync(Timeout, TestContext.Current.CancellationToken), $"Only {i} of {count} messages were sent.");
    }

    [Fact]
    public async Task A_second_set_up_that_overlaps_the_first_does_nothing_and_the_trigger_is_sent_once()
    {
        var navigation = new ChatNavigationState();
        navigation.StartOnboarding("the-trigger");
        var (chat, client) = await StartChatAsync(navigation);
        using var _ = chat;
        await using var __ = client;

        // A further set-up starts while the first is held inside its EnsureSession RPC.
        var overlapping = chat.EnsureSessionAndFlushAsync();
        _releaseFirstSetup.SetResult();
        await overlapping.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);

        await chat.SubmitAsync("closing-turn");
        await WaitForSendsAsync(1);

        // One set-up RPC, then the trigger; the next EnsureSession belongs to the closing turn.
        Assert.Equal(["the-trigger", "closing-turn"], _sent.ToArray());
        Assert.Equal(
            ["EnsureSession", "SendMessage", "EnsureSession", "SendMessage"],
            _transport.Invocations.Select(call => call.Method).ToArray());
    }

    [Fact]
    public async Task Messages_submitted_during_the_set_up_arrive_once_and_in_order()
    {
        var (chat, client) = await StartChatAsync();
        using var _ = chat;
        await using var __ = client;

        await chat.SubmitAsync("m1");
        await chat.SubmitAsync("m2");
        await chat.SubmitAsync("m3");
        _releaseFirstSetup.SetResult();

        await WaitForSendsAsync(3);
        await chat.SubmitAsync("sentinel");
        await WaitForSendsAsync(1);

        Assert.Equal(["m1", "m2", "m3", "sentinel"], _sent.ToArray());
    }

    [Fact]
    public async Task A_message_whose_send_fails_stays_queued_and_goes_out_at_the_next_set_up()
    {
        var failedOnce = 0;
        var inner = _transport.VoidInvokeHook;
        _transport.VoidInvokeHook = (method, args, ct) =>
        {
            if (method == "SendMessage" && args.Length > 1 && args[1] is "m1" && Interlocked.Exchange(ref failedOnce, 1) == 0)
                throw new IOException("the connection dropped during the send");

            return inner!(method, args, ct);
        };
        var (chat, client) = await StartChatAsync();
        using var _ = chat;
        await using var __ = client;

        await chat.SubmitAsync("m1");
        _releaseFirstSetup.SetResult();

        // The set-up that tried to send m1 failed. The next set-up must still find m1 first in line.
        await chat.EnsureSessionAndFlushAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await WaitForSendsAsync(1);
        await chat.SubmitAsync("sentinel");
        await WaitForSendsAsync(1);

        Assert.Equal(["m1", "sentinel"], _sent.ToArray());
    }
}
