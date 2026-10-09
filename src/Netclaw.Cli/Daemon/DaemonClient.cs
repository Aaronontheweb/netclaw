// -----------------------------------------------------------------------
// <copyright file="DaemonClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Actor;
using Akka.Configuration;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using R3;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Daemon;

/// <summary>Local actor facade for daemon sessions.</summary>
public sealed class DaemonClient : IAsyncDisposable
{
    public static readonly ChannelType TuiChannelType = ChannelType.Tui;
    internal static readonly TimeSpan[] DefaultReconnectDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    internal static readonly TimeSpan DefaultRpcTimeout = TimeSpan.FromSeconds(60);

    private readonly IDaemonHubTransport _transport;
    private readonly Subject<SessionOutput> _outputSubject = new();
    private readonly Subject<DaemonConnectionEvent> _connectionSubject = new();
    private readonly Channel<object> _events = Channel.CreateUnbounded<object>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lazy<Runtime> _runtime;
    private readonly Lazy<Task<ChatCloseReceipt>> _close;
    private readonly IDisposable _outputRegistration;
    private enum Lifetime { Open, Closing, Disposed }
    private Lifetime _lifetime;

    public DaemonClient(string daemonEndpoint, TimeProvider? timeProvider = null,
        TimeSpan[]? reconnectDelays = null, TimeSpan? serverTimeout = null,
        Func<Task<string?>>? accessTokenProvider = null)
        : this(daemonEndpoint, SignalRDaemonHubTransport.Create(
            $"{NormalizeEndpoint(daemonEndpoint)}/hub/session", accessTokenProvider, serverTimeout),
            timeProvider, reconnectDelays, null) { }

    internal DaemonClient(string daemonEndpoint, IDaemonHubTransport transport,
        TimeProvider? timeProvider = null, TimeSpan[]? reconnectDelays = null, TimeSpan? rpcTimeout = null)
    {
        var endpoint = NormalizeEndpoint(daemonEndpoint);
        var delays = (reconnectDelays ?? DefaultReconnectDelays).ToArray();
        if (delays.Length == 0) throw new ArgumentException("At least one reconnect delay is required.", nameof(reconnectDelays));
        _transport = transport;
        _outputRegistration = transport.On<SessionOutputDto>("ReceiveOutput", dto => _events.Writer.TryWrite(FromDto(dto)));
        transport.Closed += OnTransportClosed;
        _runtime = new Lazy<Runtime>(() =>
        {
            var system = ActorSystem.Create($"chat-client-{Guid.NewGuid():N}", ConfigurationFactory.ParseString("""
                akka.actor.provider = local
                akka.actor.guardian-supervisor-strategy = "Akka.Actor.StoppingSupervisorStrategy, Akka"
                akka.loglevel = ERROR
                """));
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var actor = system.ActorOf(Props.Create(() => new ChatClientActor(
                transport, endpoint, delays, rpcTimeout ?? DefaultRpcTimeout,
                timeProvider ?? TimeProvider.System, _events.Writer, stopped)), "client");
            return new Runtime(system, actor, stopped.Task, Task.Run(EventPumpAsync));
        });
        _close = new Lazy<Task<ChatCloseReceipt>>(() =>
        {
            lock (_runtime)
            {
                ObjectDisposedException.ThrowIf(_lifetime == Lifetime.Disposed, this);
                _lifetime = Lifetime.Closing;
                if (!_runtime.IsValueCreated) return Task.FromResult(new ChatCloseReceipt(null, []));
                var reply = new TaskCompletionSource<ChatCloseReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
                var runtime = _runtime.Value;
                runtime.Owner.Tell(new ChatClientActor.Close(reply));
                return AwaitReplyAsync(reply.Task, runtime, CancellationToken.None);
            }
        });
    }

    public Observable<SessionOutput> SessionOutput => _outputSubject.AsObservable();
    public Observable<DaemonConnectionEvent> ConnectionEvents => _connectionSubject.AsObservable();
    public bool IsConnected => _transport.IsConnected;
    internal bool HasLocalRuntime => _runtime.IsValueCreated;
    public ChatCloseReceipt? CloseReceipt => _close.IsValueCreated && _close.Value.IsCompletedSuccessfully ? _close.Value.Result : null;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => Post(ChatClientActor.RequestKind.Connect, TuiChannelType, null, null, null, cancellationToken);
    public Task<string> CreateSessionAsync(ChannelType channelType, CancellationToken cancellationToken = default)
        => SessionRequest(ChatClientActor.RequestKind.Create, channelType, null, cancellationToken);
    public Task<string> EnsureSessionAsync(ChannelType channelType, CancellationToken cancellationToken = default)
        => SessionRequest(ChatClientActor.RequestKind.Keep, channelType, null, cancellationToken);
    public Task<string> ResumeSessionAsync(string sessionId, ChannelType channelType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return SessionRequest(ChatClientActor.RequestKind.Resume, channelType, sessionId, cancellationToken);
    }
    internal Task OpenChatAsync(string? resumeSessionId, string? initialMessage)
        => Post(ChatClientActor.RequestKind.Open, TuiChannelType, resumeSessionId, initialMessage, null, CancellationToken.None);
    public Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Post(ChatClientActor.RequestKind.Send, TuiChannelType, null, text, null, cancellationToken);
    }
    public Task RespondToInteractionAsync(string callId, string selectedKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedKey);
        return Post(ChatClientActor.RequestKind.Respond, TuiChannelType, null, callId, selectedKey, cancellationToken);
    }
    public Task<ChatCloseReceipt> CloseAsync() => _close.Value;

    private async Task<string> SessionRequest(ChatClientActor.RequestKind kind, ChannelType channelType,
        string? sessionId, CancellationToken token)
        => (string)(await Post(kind, channelType, sessionId, null, null, token))!;

    private Task<object?> Post(ChatClientActor.RequestKind kind, ChannelType channelType, string? sessionId,
        string? text, string? selectedKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var runtime = GetRuntime();
        var request = new ChatClientActor.Request(kind, channelType, sessionId, text, selectedKey, token);
        // Post before the first await. Enter and Ctrl+Q retain their input order.
        runtime.Owner.Tell(request);
        return AwaitReplyAsync(request.Reply.Task, runtime, token);
    }
    private Runtime GetRuntime()
    {
        // Serialize runtime creation and disposal. The actor owns connection and session state.
        lock (_runtime)
        {
            ObjectDisposedException.ThrowIf(_lifetime == Lifetime.Disposed, this);
            if (_lifetime == Lifetime.Closing) throw new InvalidOperationException("The chat client is closed.");
            var runtime = _runtime.Value;
            if (runtime.Stopped.IsCompleted) throw new InvalidOperationException("The chat client actor stopped.");
            return runtime;
        }
    }
    private static async Task<T> AwaitReplyAsync<T>(Task<T> reply, Runtime runtime, CancellationToken token)
    {
        var completed = await Task.WhenAny(reply, runtime.Stopped).WaitAsync(token).ConfigureAwait(false);
        if (completed != reply) throw new InvalidOperationException("The chat client actor stopped before it returned a result.");
        return await reply.ConfigureAwait(false);
    }
    private Task OnTransportClosed(Exception? error)
    {
        if (_runtime.IsValueCreated) _runtime.Value.Owner.Tell(new ChatClientActor.TransportDropped(error));
        return Task.CompletedTask;
    }
    private async Task EventPumpAsync()
    {
        // Subscriber code runs outside the actor and cannot block its close deadline.
        await foreach (var value in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (value is DaemonConnectionEvent connection) _connectionSubject.OnNext(connection);
            else if (value is SessionOutput output) _outputSubject.OnNext(output);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Runtime? runtime;
        lock (_runtime)
        {
            if (_lifetime == Lifetime.Disposed) return;
            _lifetime = Lifetime.Disposed;
            runtime = _runtime.IsValueCreated ? _runtime.Value : null;
        }
        _transport.Closed -= OnTransportClosed;
        _outputRegistration.Dispose();
        if (runtime is not null) await runtime.System.Terminate().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        if (runtime is not null) await runtime.Pump.ConfigureAwait(false);
        _outputSubject.Dispose();
        _connectionSubject.Dispose();
    }
    private static string NormalizeEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return endpoint.TrimEnd('/');
    }
    internal static SessionOutput FromDto(SessionOutputDto dto) => SessionOutputDtoMapper.FromDto(dto);
    private sealed record Runtime(ActorSystem System, IActorRef Owner, Task Stopped, Task Pump);
}
