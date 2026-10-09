// -----------------------------------------------------------------------
// <copyright file="ChatClientActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Net;
using System.Threading.Channels;
using Akka.Actor;
using Microsoft.AspNetCore.SignalR;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Daemon;

public enum InputDeliveryStatus { Unsent, Rejected, Unconfirmed }
public sealed record UndeliveredInput(string Text, InputDeliveryStatus Status, string Reason)
{
    public string? SessionId { get; init; }
}
public sealed record ChatCloseReceipt(string? SessionId, ImmutableArray<UndeliveredInput> Inputs)
{
    public string Notice => Inputs.Length == 0 ? "All submitted text has daemon admission confirmation."
        : $"{Inputs.Count(input => input.Status == InputDeliveryStatus.Unsent)} unsent; "
        + $"{Inputs.Count(input => input.Status == InputDeliveryStatus.Rejected)} rejected; "
        + $"{Inputs.Count(input => input.Status == InputDeliveryStatus.Unconfirmed)} delivery unconfirmed. "
        + $"Check session {string.Join(", ", Inputs.Where(input => input.Status == InputDeliveryStatus.Unconfirmed).Select(input => input.SessionId ?? "(not created)").Distinct().DefaultIfEmpty(SessionId ?? "(not created)"))} before you resend.";
}

internal sealed class ChatClientActor : ReceiveActor, IWithTimers
{
    internal enum RequestKind { Connect, Open, Create, Keep, Resume, Send, Respond, Recover }
    internal sealed record Request(RequestKind Kind, ChannelType ChannelType, string? SessionId,
        string? Text, string? SelectedKey, CancellationToken Token)
    {
        public TaskCompletionSource<object?> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal sealed record Close(TaskCompletionSource<ChatCloseReceipt> Reply);
    internal sealed record TransportDropped(Exception? Error);
    private enum Operation { None, Connect, Bind, Send, Respond }
    private sealed record Completed(long Id, Operation Operation, object? Value, Exception? Error);
    private sealed record Cancelled(long Id);
    private sealed record Deadline(long Id);
    private sealed record Retry;
    private sealed record CloseDeadline;
    private static readonly object RetryKey = new();
    private static readonly object OperationKey = new();
    private static readonly object CloseKey = new();
    private static readonly TimeSpan[] AttachmentDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly IDaemonHubTransport _transport;
    private readonly string _endpoint;
    private readonly TimeSpan[] _delays;
    private readonly TimeSpan _rpcTimeout;
    private readonly TimeProvider _clock;
    private readonly ChannelWriter<object> _events;
    private readonly TaskCompletionSource _stopped;
    private readonly Queue<Request> _queue = new();
    private readonly List<UndeliveredInput> _unresolved = [];
    private readonly List<TaskCompletionSource<ChatCloseReceipt>> _closers = [];
    private Request? _current;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenRegistration _callerCancellation;
    private long _operationId;
    private Operation _operation;
    private int _attempt;
    private string? _sessionId;
    private ChannelType _channelType = ChannelType.Tui;
    private string? _initial;
    private bool _attached;
    private bool _interactive;
    private bool _hasConnected;
    private bool _recover;
    private bool _aborted;
    private ChatCloseReceipt? _receipt;
    public ITimerScheduler Timers { get; set; } = null!;

    public ChatClientActor(IDaemonHubTransport transport, string endpoint, TimeSpan[] delays,
        TimeSpan rpcTimeout, TimeProvider clock, ChannelWriter<object> events, TaskCompletionSource stopped)
    {
        _transport = transport;
        _endpoint = endpoint;
        _delays = delays.ToArray();
        _rpcTimeout = rpcTimeout;
        _clock = clock;
        _events = events;
        _stopped = stopped;
        Idle();
    }

    private void Controls()
    {
        Receive<Request>(request =>
        {
            if (_closers.Count > 0 || _receipt is not null)
                request.Reply.TrySetException(new InvalidOperationException("The chat client is closed."));
            else { _queue.Enqueue(request); if (_current is null) Next(); }
        });
        Receive<Close>(close =>
        {
            if (_receipt is not null) { close.Reply.TrySetResult(_receipt); return; }
            _closers.Add(close.Reply);
            if (_closers.Count == 1)
            {
                Timers.StartSingleTimer(CloseKey, new CloseDeadline(), TimeSpan.FromSeconds(2));
                Publish(DaemonConnectionState.Closing, "Confirming daemon admission...");
            }
            if (_current is null) Next();
        });
        Receive<CloseDeadline>(_ => FinishClose());
        Receive<TransportDropped>(drop =>
        {
            if (_transport.IsConnected || _receipt is not null) return;
            _attached = false;
            _recover = _sessionId is not null;
            Publish(DaemonConnectionState.TransportClosed, $"Connection to daemon dropped: {drop.Error?.Message ?? "connection closed"}");
            if (_current is null) Next();
        });
        Receive<Cancelled>(cancel => Abort(cancel.Id, new OperationCanceledException("The request was cancelled. Delivery may be unconfirmed.")));
    }
    private void Idle() { Controls(); }
    private void Operating()
    {
        Controls();
        Receive<Completed>(CompleteOperation);
        Receive<Deadline>(deadline => Abort(deadline.Id,
            new OperationCanceledException("The daemon response timed out. Delivery may be unconfirmed.")));
    }
    private void Retrying()
    {
        Controls();
        Receive<Retry>(_ => Continue());
    }
    private void Closed()
    {
        Controls();
        Receive<Completed>(_ => { });
        Receive<Deadline>(_ => { });
        Receive<Retry>(_ => { });
    }

    private void Next()
    {
        if (_receipt is not null) return;
        if (_recover && _closers.Count == 0)
        {
            _recover = false;
            Begin(new Request(RequestKind.Recover, _channelType, _sessionId, null, null, CancellationToken.None));
            return;
        }
        while (_queue.TryDequeue(out var request))
        {
            if (request.Token.IsCancellationRequested) { request.Reply.TrySetCanceled(request.Token); continue; }
            Begin(request);
            return;
        }
        if (_initial is { } initial)
        {
            _initial = null;
            Begin(new Request(RequestKind.Send, _channelType, null, initial, null, CancellationToken.None));
            return;
        }
        Become(Idle);
        if (_closers.Count > 0) FinishClose();
    }
    private void Begin(Request request)
    {
        _current = request;
        _attempt = 0;
        _aborted = false;
        _operation = Operation.None;
        if (request.Kind is RequestKind.Open or RequestKind.Create or RequestKind.Resume)
        {
            _sessionId = request.Kind == RequestKind.Create ? null : request.SessionId;
            _attached = false;
        }
        if (request.Kind is RequestKind.Open)
        {
            _initial = request.Text;
        }
        if (request.Kind is RequestKind.Open or RequestKind.Create or RequestKind.Keep or RequestKind.Resume)
        {
            _interactive = request.Kind == RequestKind.Open;
            _channelType = request.ChannelType;
        }
        Continue();
    }
    private void Continue()
    {
        if (_current is not { } request || _receipt is not null) return;
        if (request.Token.IsCancellationRequested) { End(new OperationCanceledException(request.Token)); return; }
        if (!_transport.IsConnected)
        {
            _attached = false;
            Publish(_hasConnected ? DaemonConnectionState.Reconnecting : DaemonConnectionState.Connecting,
                $"Connecting to daemon at {_endpoint}...");
            Start(Operation.Connect, token => ConnectTransport(token));
            return;
        }
        if (request.Kind == RequestKind.Connect || request.Kind == RequestKind.Open && _sessionId is null && _initial is null)
        { End(null); return; }
        if (!_attached || request.Kind is RequestKind.Create or RequestKind.Keep or RequestKind.Resume)
        {
            if (!_interactive && _sessionId is null && request.Kind is RequestKind.Send or RequestKind.Respond)
            { End(new InvalidOperationException("Session not initialized. Call CreateSessionAsync first.")); return; }
            Start(Operation.Bind, token => BindTransport(_sessionId, _channelType, token));
            return;
        }
        Dispatch(request);
    }
    private void Dispatch(Request request)
    {
        switch (request.Kind)
        {
            case RequestKind.Send:
                Start(Operation.Send, token => InvokeTransport("SendMessage", [_sessionId, request.Text], token));
                break;
            case RequestKind.Respond:
                Start(Operation.Respond, token => InvokeTransport("RespondToInteraction", [_sessionId, request.Text, request.SelectedKey], token));
                break;
            default: End(null); break;
        }
    }
    private async Task<object?> ConnectTransport(CancellationToken token)
    { await _transport.StartAsync(token).ConfigureAwait(false); return null; }
    private async Task<object?> BindTransport(string? sessionId, ChannelType channelType, CancellationToken token)
        => await _transport.InvokeAsync<SessionEnsureResultDto>("EnsureSession", [sessionId, channelType.ToWireValue()], token).ConfigureAwait(false);
    private async Task<object?> InvokeTransport(string method, object?[] arguments, CancellationToken token)
    { await _transport.InvokeAsync(method, arguments, token).ConfigureAwait(false); return null; }

    private void Start(Operation operation, Func<CancellationToken, Task<object?>> action)
    {
        _operation = operation;
        var id = ++_operationId;
        var self = Self;
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_current!.Token);
        _callerCancellation = _current.Token.Register(() => self.Tell(new Cancelled(id)));
        Timers.StartSingleTimer(OperationKey, new Deadline(id), _rpcTimeout);
        Become(Operating);
        Task<object?> task;
        try { task = action(_operationCancellation.Token); }
        catch (Exception error) { task = Task.FromException<object?>(error); }
        task.PipeTo(self, success: value => new Completed(id, operation, value, null),
            failure: error => new Completed(id, operation, null, error));
    }
    private void CompleteOperation(Completed completed)
    {
        if (completed.Id != _operationId || _current is null) return;
        Timers.Cancel(OperationKey);
        _callerCancellation.Dispose();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        if (_aborted) { End(null); return; }
        if (completed.Error is { } error)
        {
            if (error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
                error = new InvalidOperationException("Authentication failed. Run 'netclaw pair <endpoint>' to re-pair this device.", error);
            else if (completed.Operation is Operation.Connect or Operation.Bind && !_current.Token.IsCancellationRequested)
            {
                var recovery = _current.Kind == RequestKind.Recover;
                var limit = recovery ? 20 : _interactive ? int.MaxValue : _delays.Length;
                if (++_attempt < limit)
                {
                    var delays = completed.Operation == Operation.Bind && _interactive ? AttachmentDelays : _delays;
                    var index = completed.Operation == Operation.Bind && _interactive ? _attempt - 1 : _attempt;
                    var delay = delays[Math.Min(index, delays.Length - 1)];
                    Become(Retrying);
                    Timers.StartSingleTimer(RetryKey, new Retry(), delay);
                    Publish(DaemonConnectionState.Reconnecting, $"Retry {_attempt} in {delay.TotalSeconds:0}s: {error.Message}", _attempt, limit);
                    return;
                }
            }
            End(error);
            return;
        }
        if (completed.Operation == Operation.Connect)
        {
            if (!_transport.IsConnected) { Continue(); return; }
            _hasConnected = true;
            Publish(DaemonConnectionState.Connected, $"Connected to daemon at {_endpoint}.");
            Continue();
        }
        else if (completed.Operation == Operation.Bind)
        {
            var result = (SessionEnsureResultDto)completed.Value!;
            if (result.TextAdmissionVersion != SessionEnsureResultDto.SupportedTextAdmissionVersion)
            {
                _attached = false;
                End(new NotSupportedException($"The daemon has unsupported text admission version {result.TextAdmissionVersion}. Upgrade the daemon."));
                return;
            }
            _sessionId = result.SessionId;
            if (!_transport.IsConnected) { Continue(); return; }
            _attached = true;
            _recover = false;
            _attempt = 0;
            Publish(DaemonConnectionState.Connected, $"{(_current.Kind == RequestKind.Recover ? "Reconnected" : "Attached")} to daemon session {_sessionId}.");
            Dispatch(_current);
        }
        else End(null);
    }
    private void Abort(long id, Exception error)
    {
        if (id != _operationId || _current is null || _aborted || _receipt is not null) return;
        if (_operationCancellation is null) { End(error); return; }
        _aborted = true;
        RecordFailure(_current, error, _operation is Operation.Send or Operation.Respond ? InputDeliveryStatus.Unconfirmed : InputDeliveryStatus.Unsent);
        _current.Reply.TrySetException(error);
        _ = _current.Reply.Task.Exception;
        _operationCancellation?.Cancel();
        // Wait for the task result. Cancellation alone does not end transport side effects.
    }
    private void End(Exception? error)
    {
        if (_current is not { } request) return;
        Timers.Cancel(RetryKey);
        if (error is not null)
        {
            var status = _operation is Operation.Send or Operation.Respond
                ? error is HubException && error.Message.Contains(SessionEnsureResultDto.TextRejectionPrefix, StringComparison.Ordinal)
                    ? InputDeliveryStatus.Rejected : InputDeliveryStatus.Unconfirmed
                : InputDeliveryStatus.Unsent;
            RecordFailure(request, error, status);
            if (request.Kind == RequestKind.Send) _attached = false;
            request.Reply.TrySetException(error);
            _ = request.Reply.Task.Exception;
            if (request.Kind is RequestKind.Open or RequestKind.Recover || error is NotSupportedException)
                Publish(DaemonConnectionState.Disconnected, error.Message);
            if (request.Kind == RequestKind.Open && _initial is { } initial)
            {
                RecordFailure(new Request(RequestKind.Send, _channelType, null, initial, null, CancellationToken.None), error, InputDeliveryStatus.Unsent);
                _initial = null;
            }
        }
        else request.Reply.TrySetResult(_sessionId);
        _current = null;
        _aborted = false;
        Next();
    }
    private void RecordFailure(Request request, Exception error, InputDeliveryStatus status)
    {
        if (request.Kind is not (RequestKind.Send or RequestKind.Respond)) return;
        var text = request.Kind == RequestKind.Send ? request.Text! : $"Interaction {request.Text}: {request.SelectedKey}";
        _unresolved.Add(new UndeliveredInput(text, status, error.Message) { SessionId = _sessionId });
        if (request.Kind == RequestKind.Respond) return;
        _events.TryWrite(new ErrorOutput
        {
            SessionId = new SessionId(_sessionId ?? "signalr/client"),
            TimestampMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            Message = $"Message {status.ToString().ToLowerInvariant()}: {error.Message}"
        });
    }
    private void FinishClose()
    {
        if (_receipt is not null) return;
        Timers.CancelAll();
        var error = new OperationCanceledException("The chat closed before admission confirmation.");
        if (_current is { } current)
        {
            if (!_aborted) RecordFailure(current, error, _operation is Operation.Send or Operation.Respond ? InputDeliveryStatus.Unconfirmed : InputDeliveryStatus.Unsent);
            current.Reply.TrySetException(error);
            _ = current.Reply.Task.Exception;
        }
        while (_queue.TryDequeue(out var pending))
        {
            if (!pending.Token.IsCancellationRequested) RecordFailure(pending, error, InputDeliveryStatus.Unsent);
            pending.Reply.TrySetException(error);
            _ = pending.Reply.Task.Exception;
        }
        if (_initial is { } initial)
            _unresolved.Add(new UndeliveredInput(initial, InputDeliveryStatus.Unsent, error.Message) { SessionId = _sessionId });
        _initial = null;
        _current = null;
        _operationCancellation?.Cancel();
        _receipt = new ChatCloseReceipt(_sessionId, _unresolved.ToImmutableArray());
        foreach (var closer in _closers) closer.TrySetResult(_receipt);
        Become(Closed);
    }
    private void Publish(DaemonConnectionState state, string message, int? attempt = null, int? limit = null)
        => _events.TryWrite(new DaemonConnectionEvent(state, _endpoint, message, attempt, limit) { SessionId = _sessionId });
    protected override void PostStop()
    {
        FinishClose();
        _callerCancellation.Dispose();
        _operationCancellation?.Dispose();
        _stopped.TrySetResult();
        base.PostStop();
    }
}
