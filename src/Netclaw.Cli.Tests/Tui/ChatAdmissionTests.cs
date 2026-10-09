// -----------------------------------------------------------------------
// <copyright file="ChatAdmissionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tests.Cli;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using R3;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ChatAdmissionTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();
    public void Dispose() => _directory.Dispose();
    private NetclawPaths Paths
    {
        get { var paths = new NetclawPaths(_directory.Path); paths.EnsureDirectoriesExist(); return paths; }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_Enter_then_repeated_CtrlQ_waits_for_admission_without_a_model_response(bool heldAdmission)
    {
        var entered = Gate();
        var release = Gate();
        if (!heldAdmission) release.SetResult();
        var transport = new FakeDaemonHubTransport
        {
            VoidInvokeHook = (_, _, token) => { entered.TrySetResult(); return release.Task.WaitAsync(token); }
        };
        await using var client = new DaemonClient("http://localhost", transport, reconnectDelays: [TimeSpan.Zero]);
        var navigation = new ChatNavigationState();
        var input = new VirtualInputSource();
        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(new VirtualTerminal(100, 30));
        services.AddTerminaVirtualInput(input);
        services.AddTermina(ChatViewModel.Route, routes => routes.RegisterRoute<ChatPage, ChatViewModel>(
            ChatViewModel.Route, provider => new ChatPage(provider.GetRequiredService<IAnsiTerminal>()),
            _ => new ChatViewModel(client, TimeProvider.System, new ModelCapabilities { ModelId = "test" }, navigation, Paths)));
        await using var provider = services.BuildServiceProvider();
        input.EnqueueString("typed-before-quit");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);
        input.EnqueueString("rejected-after-quit");
        input.EnqueueKey(ConsoleKey.Enter);
        var run = provider.GetRequiredService<TerminaApplication>().RunAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (heldAdmission) Assert.False(run.IsCompleted);
            release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Empty(Assert.IsType<ChatCloseReceipt>(navigation.CloseReceipt).Inputs);
            Assert.Same(client.CloseReceipt, navigation.CloseReceipt);
            var send = Assert.Single(transport.Invocations, call => call.Method == "SendMessage");
            Assert.Equal("typed-before-quit", send.Args[1]);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_dormant_client_does_not_create_an_actor_system(bool close)
    {
        var transport = new FakeDaemonHubTransport();
        await using var client = new DaemonClient("http://localhost", transport);
        if (close) Assert.Empty((await client.CloseAsync()).Inputs);
        Assert.False(client.HasLocalRuntime);
        Assert.Equal(0, transport.StartAttempts);
        Assert.Empty(transport.Invocations);
    }

    [Fact]
    public async Task A_late_admission_response_does_not_restore_generation_after_turn_completion()
    {
        var release = Gate();
        var completed = Gate();
        var transport = new FakeDaemonHubTransport();
        transport.VoidInvokeHook = (_, args, token) =>
        {
            transport.PushOutput(new SessionOutputDto { Type = "turn_completed", SessionId = (string)args[0]!, TurnNumber = new TurnNumber(1) });
            return release.Task.WaitAsync(token);
        };
        await using var client = new DaemonClient("http://localhost", transport, reconnectDelays: [TimeSpan.Zero]);
        using var chat = new ChatViewModel(client, TimeProvider.System, new ModelCapabilities { ModelId = "test" }, new ChatNavigationState(), Paths);
        chat.OnActivated();
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        using var status = chat.StatusMessage.Subscribe(value =>
        {
            if (value == "Ready" && transport.Invocations.Any(call => call.Method == "SendMessage")) completed.TrySetResult();
        });
        try
        {
            await chat.SubmitAsync("first");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(chat.IsGenerating.Value);
            release.TrySetResult();
            await client.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.False(chat.IsGenerating.Value);
            Assert.Equal("Ready", chat.StatusMessage.Value);
        }
        finally { release.TrySetResult(); }
    }
}
