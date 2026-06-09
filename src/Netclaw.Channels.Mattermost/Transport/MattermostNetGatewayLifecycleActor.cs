// -----------------------------------------------------------------------
// <copyright file="MattermostNetGatewayLifecycleActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Microsoft.Extensions.Logging;

namespace Netclaw.Channels.Mattermost.Transport;

internal interface IMattermostGatewayEventSink
{
    Task PublishMessageAsync(MattermostGatewayMessage message);

    Task PublishCleanReconnectRequiredAsync(string reason);

    Task PublishConnectionRestoredAsync(MattermostGatewaySnapshot snapshot);
}

internal interface IMattermostGatewayTransport
{
    event Func<MattermostGatewayMessage, Task> MessageReceived;

    event Func<Task> Connected;

    event Func<MattermostGatewayDisconnect, Task> Disconnected;

    event Func<string, Task> LogReceived;

    bool IsConnected { get; }

    Task<MattermostBotIdentity> StartAsync(string serverUrl, string botToken, CancellationToken cancellationToken = default);

    Task StopAsync();
}

internal sealed record MattermostBotIdentity(string UserId, string Username);

internal sealed record MattermostGatewayDisconnect(string? Reason, Exception? Exception = null);

internal sealed class MattermostNetGatewayLifecycleActor : ReceiveActor
{
    private const string DisconnectedDetail = "Mattermost gateway disconnected.";

    private readonly IMattermostGatewayTransport _transport;
    private readonly IMattermostGatewayEventSink _eventSink;
    private readonly ILogger _logger;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private bool _isReadyBehavior;
    private long _connectAttempt;
    private IActorRef _self = ActorRefs.Nobody;
    private IActorRef? _pendingConnectReplyTo;
    private MattermostUserId? _botUserId;
    private string? _botUsername;
    private string? _healthDetail = DisconnectedDetail;
    private bool _cleanReconnectEmitted;

    private bool _autoReconnect;
    private string? _retryServerUrl;
    private string? _retryBotToken;
    private TimeSpan _retryDelay;
    private ICancelable? _retryTimer;

    public MattermostNetGatewayLifecycleActor(
        IMattermostGatewayTransport transport,
        IMattermostGatewayEventSink eventSink,
        ILogger logger)
    {
        _transport = transport;
        _eventSink = eventSink;
        _logger = logger;

        Become(Disconnected);
    }

    public static Props CreateProps(
        IMattermostGatewayTransport transport,
        IMattermostGatewayEventSink eventSink,
        ILogger logger) =>
        Props.Create(() => new MattermostNetGatewayLifecycleActor(transport, eventSink, logger));

    protected override void PreStart()
    {
        _self = Self;
        _transport.MessageReceived += OnMessageReceivedAsync;
        _transport.Connected += OnConnectedAsync;
        _transport.Disconnected += OnDisconnectedAsync;
        _transport.LogReceived += OnLogReceivedAsync;
        base.PreStart();
    }

    protected override void PostStop()
    {
        _transport.MessageReceived -= OnMessageReceivedAsync;
        _transport.Connected -= OnConnectedAsync;
        _transport.Disconnected -= OnDisconnectedAsync;
        _transport.LogReceived -= OnLogReceivedAsync;
        CancelRetryTimer();
        base.PostStop();
    }

    private void Disconnected()
    {
        _isReadyBehavior = false;
        ReceiveCommon();
        Receive<Connect>(connect =>
        {
            StoreRetryCredentials(connect.ServerUrl, connect.BotToken);
            _autoReconnect = true;
            StartConnecting(connect.ServerUrl, connect.BotToken, Sender);
        });
        Receive<Disconnect>(_ =>
        {
            CancelAutoReconnect();
            StartDisconnecting(Sender);
        });
        Receive<RetryConnect>(_ =>
        {
            if (!_autoReconnect || _retryServerUrl is null || _retryBotToken is null)
                return;

            _logger.LogInformation("Attempting Mattermost reconnect (delay was {Delay}).", _retryDelay);
            StartConnecting(_retryServerUrl, _retryBotToken, ActorRefs.Nobody);
        });
        Receive<MattermostConnected>(_ => RequestCleanReconnect(
            "Mattermost gateway reconnected outside a clean startup cycle; forcing a clean reconnect."));
        Receive<MattermostDisconnected>(HandleDisconnectedWhileNotReady);
        ReceiveNotReadyIngress();
        ReceiveUnexpected(nameof(Disconnected));
    }

    private void Connecting()
    {
        _isReadyBehavior = false;
        ReceiveCommon();
        Receive<Connect>(_ => Sender.Tell(new Status.Failure(new InvalidOperationException(
            "Mattermost gateway connect is already in progress."))));
        Receive<Disconnect>(_ => StartDisconnecting(Sender));
        Receive<MattermostStartSucceeded>(HandleStartSucceeded);
        Receive<MattermostStartFailed>(HandleStartFailed);
        Receive<MattermostConnected>(_ => _healthDetail = "Mattermost gateway connected; completing startup.");
        Receive<MattermostDisconnected>(HandleDisconnectedWhileConnecting);
        ReceiveNotReadyIngress();
        ReceiveUnexpected(nameof(Connecting));
    }

    private void Ready()
    {
        _isReadyBehavior = true;
        ReceiveCommon();
        Receive<Connect>(_ => Sender.Tell(CurrentSnapshot()));
        Receive<Disconnect>(_ => StartDisconnecting(Sender));
        Receive<MattermostConnected>(_ => _healthDetail = null);
        Receive<MattermostDisconnected>(HandleDisconnectedWhileReady);
        Receive<MattermostMessageReceived>(HandleMessageReceived);
        ReceiveUnexpected(nameof(Ready));
    }

    private void CleanReconnectRequired()
    {
        _isReadyBehavior = false;
        ReceiveCommon();
        Receive<Connect>(_ => Sender.Tell(new Status.Failure(new ChannelConnectException(
            ChannelConnectFailureKind.Transient,
            "Mattermost gateway requires a clean disconnect before reconnecting."))));
        Receive<Disconnect>(_ => StartDisconnecting(Sender));
        Receive<MattermostConnected>(_ => { });
        Receive<MattermostDisconnected>(HandleDisconnectedWhileNotReady);
        ReceiveNotReadyIngress();
        ReceiveUnexpected(nameof(CleanReconnectRequired));
    }

    private void Disconnecting()
    {
        _isReadyBehavior = false;
        ReceiveCommon();
        Receive<Connect>(_ => Sender.Tell(new Status.Failure(new InvalidOperationException(
            "Mattermost gateway disconnect is already in progress."))));
        Receive<Disconnect>(_ => Sender.Tell(new Status.Failure(new InvalidOperationException(
            "Mattermost gateway disconnect is already in progress."))));
        Receive<MattermostStopSucceeded>(HandleStopSucceeded);
        Receive<MattermostStopFailed>(HandleStopFailed);
        Receive<MattermostConnected>(_ => { });
        Receive<MattermostDisconnected>(_ => { });
        ReceiveNotReadyIngress();
        ReceiveUnexpected(nameof(Disconnecting));
    }

    private void ReceiveCommon()
    {
        Receive<GetSnapshot>(_ => Sender.Tell(CurrentSnapshot()));
        Receive<MattermostLogReceived>(HandleLogReceived);
        Receive<DispatchFailed>(HandleDispatchFailed);
    }

    private void ReceiveNotReadyIngress()
    {
        Receive<MattermostMessageReceived>(DropMessageReceived);
    }

    private void ReceiveUnexpected(string behaviorName) =>
        ReceiveAny(message => HandleWrongBehaviorMessage(message, behaviorName));

    private Task OnMessageReceivedAsync(MattermostGatewayMessage message)
    {
        _self.Tell(new MattermostMessageReceived(message));
        return Task.CompletedTask;
    }

    private Task OnConnectedAsync()
    {
        _self.Tell(MattermostConnected.Instance);
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MattermostGatewayDisconnect disconnect)
    {
        _self.Tell(new MattermostDisconnected(disconnect));
        return Task.CompletedTask;
    }

    private Task OnLogReceivedAsync(string message)
    {
        _self.Tell(new MattermostLogReceived(message));
        return Task.CompletedTask;
    }

    private void StartConnecting(string serverUrl, string botToken, IActorRef replyTo)
    {
        _healthDetail = "Mattermost gateway connecting.";
        _botUserId = null;
        _botUsername = null;
        _cleanReconnectEmitted = false;

        // Normalize Nobody to null: the auto-retry path connects with
        // ActorRefs.Nobody, and storing it verbatim made the
        // `_pendingConnectReplyTo is null` isRetry checks misfire — most
        // visibly keeping ConnectionRestored from publishing after an
        // auto-retry recovery. Mirrors the same fix in the Discord lifecycle
        // actor, where the skew also produced a stuck CleanReconnectRequired
        // state.
        _pendingConnectReplyTo = replyTo.IsNobody() ? null : replyTo;

        var attempt = ++_connectAttempt;
        Become(Connecting);
        BeginStart(serverUrl, botToken, attempt);
    }

    private void BeginStart(string serverUrl, string botToken, long attempt)
    {
        var self = Self;
        Task<MattermostBotIdentity> startTask;
        try
        {
            startTask = _transport.StartAsync(serverUrl, botToken, CancellationToken.None);
        }
        catch (Exception ex)
        {
            self.Tell(new MattermostStartFailed(attempt, ex));
            return;
        }

        startTask.ContinueWith(
            task => self.Tell(task.IsCompletedSuccessfully
                ? new MattermostStartSucceeded(attempt, task.Result)
                : new MattermostStartFailed(attempt, UnwrapTaskException(task))),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleStartSucceeded(MattermostStartSucceeded started)
    {
        if (started.Attempt != _connectAttempt)
            return;

        if (string.IsNullOrWhiteSpace(started.Identity.UserId)
            || string.IsNullOrWhiteSpace(started.Identity.Username))
        {
            var failure = new ChannelConnectException(
                ChannelConnectFailureKind.Transient,
                "Mattermost gateway connected but the current bot identity is unavailable.");
            _healthDetail = failure.Message;
            FailPendingConnect(failure);
            ScheduleRetryIfEnabled();
            Become(Disconnected);
            return;
        }

        if (!_transport.IsConnected)
        {
            var failure = new ChannelConnectException(
                ChannelConnectFailureKind.Transient,
                "Mattermost gateway started but the WebSocket is not connected.");
            _healthDetail = failure.Message;
            FailPendingConnect(failure);
            ScheduleRetryIfEnabled();
            Become(Disconnected);
            return;
        }

        _botUserId = new MattermostUserId(started.Identity.UserId);
        _botUsername = started.Identity.Username;
        _retryDelay = TimeSpan.Zero;
        var isRetry = _pendingConnectReplyTo is null;
        TransitionToReady();
        CompletePendingConnect(CurrentSnapshot());

        if (isRetry)
            Dispatch("Mattermost connection restored", () => _eventSink.PublishConnectionRestoredAsync(CurrentSnapshot()));
    }

    private void HandleStartFailed(MattermostStartFailed failed)
    {
        if (failed.Attempt != _connectAttempt)
            return;

        var classified = MattermostConnectFailureClassifier.Classify(failed.Exception);
        _healthDetail = classified.Message;
        FailPendingConnect(classified);

        if (classified.IsFatal)
        {
            _logger.LogError(classified,
                "Mattermost connect hit a fatal failure; auto-reconnect disabled. {Reason}",
                classified.Message);
            _autoReconnect = false;
        }
        else
        {
            ScheduleRetryIfEnabled();
        }

        Become(Disconnected);
    }

    private void StartDisconnecting(IActorRef replyTo, bool preserveAutoReconnect = false)
    {
        CancelRetryTimer();
        if (!preserveAutoReconnect)
            _autoReconnect = false;

        ++_connectAttempt;
        _healthDetail = "Mattermost gateway disconnecting.";
        _cleanReconnectEmitted = false;
        FailPendingConnect(new OperationCanceledException("Mattermost gateway disconnect requested."));
        Become(Disconnecting);
        BeginStop(replyTo);
    }

    private void BeginStop(IActorRef replyTo)
    {
        var self = Self;
        Task stopTask;
        try
        {
            stopTask = _transport.StopAsync();
        }
        catch (Exception ex)
        {
            self.Tell(new MattermostStopFailed(replyTo, ex));
            return;
        }

        stopTask.ContinueWith(
            task => self.Tell(task.IsCompletedSuccessfully
                ? new MattermostStopSucceeded(replyTo)
                : new MattermostStopFailed(replyTo, UnwrapTaskException(task))),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleStopSucceeded(MattermostStopSucceeded stopped)
    {
        _healthDetail = DisconnectedDetail;
        _botUserId = null;
        _botUsername = null;
        ScheduleRetryIfEnabled();
        Become(Disconnected);
        if (!stopped.ReplyTo.Equals(ActorRefs.Nobody))
            stopped.ReplyTo.Tell(CurrentSnapshot());
    }

    private void HandleStopFailed(MattermostStopFailed failed)
    {
        _healthDetail = failed.Exception.Message;
        ScheduleRetryIfEnabled();
        Become(Disconnected);
        if (!failed.ReplyTo.Equals(ActorRefs.Nobody))
            failed.ReplyTo.Tell(new Status.Failure(failed.Exception));
    }

    private void HandleDisconnectedWhileReady(MattermostDisconnected disconnected)
    {
        var detail = EndSentence(BuildDisconnectDetail(disconnected.Disconnect));
        RequestCleanReconnect(detail + " A clean reconnect is required.");
    }

    private void HandleDisconnectedWhileConnecting(MattermostDisconnected disconnected)
    {
        _healthDetail = BuildDisconnectDetail(disconnected.Disconnect);
    }

    private void HandleDisconnectedWhileNotReady(MattermostDisconnected disconnected)
    {
        _healthDetail = BuildDisconnectDetail(disconnected.Disconnect);
    }

    private void TransitionToReady()
    {
        _healthDetail = null;
        _cleanReconnectEmitted = false;
        Become(Ready);
    }

    private void HandleMessageReceived(MattermostMessageReceived received)
    {
        if (!IsReadyCore())
        {
            DropMessageReceived(received);
            return;
        }

        Dispatch(
            "Mattermost message " + received.Message.EventId.Value,
            () => _eventSink.PublishMessageAsync(received.Message));
    }

    private void DropMessageReceived(MattermostMessageReceived received)
    {
        _logger.LogWarning(
            "Dropping Mattermost message {EventId} while gateway is not ready: {Reason}",
            received.Message.EventId.Value,
            CurrentSnapshot().HealthDetail);
    }

    private void RequestCleanReconnect(string reason)
    {
        _healthDetail = reason;
        FailPendingConnect(new ChannelConnectException(ChannelConnectFailureKind.Transient, reason));

        if (!_cleanReconnectEmitted)
        {
            _cleanReconnectEmitted = true;
            _logger.LogWarning("Gateway requested clean reconnect: {Reason}", reason);
            Dispatch("Mattermost clean reconnect", () => _eventSink.PublishCleanReconnectRequiredAsync(reason));
        }

        _retryDelay = TimeSpan.Zero;
        StartDisconnecting(ActorRefs.Nobody, preserveAutoReconnect: true);
    }

    private void HandleLogReceived(MattermostLogReceived received) =>
        _logger.LogDebug("[Mattermost.NET] {Message}", received.Message);

    private void HandleWrongBehaviorMessage(object message, string behaviorName)
    {
        switch (message)
        {
            case IMattermostGatewayConnectWork connectWork:
                IgnoreWrongBehaviorConnectWork(connectWork, behaviorName);
                break;
            case IMattermostGatewayStopWork stopWork:
                IgnoreWrongBehaviorStopWork(stopWork, behaviorName);
                break;
            case IMattermostGatewayInternalMessage:
                _logger.LogDebug(
                    "Ignoring Mattermost gateway internal message {MessageType} while in {State} state.",
                    message.GetType().Name,
                    behaviorName);
                break;
            default:
                _logger.LogWarning(
                    "Ignoring unexpected Mattermost gateway message {MessageType} while in {State} state.",
                    message.GetType().Name,
                    behaviorName);
                break;
        }
    }

    private void IgnoreWrongBehaviorConnectWork(
        IMattermostGatewayConnectWork connectWork,
        string behaviorName)
    {
        _logger.LogDebug(
            "Ignoring Mattermost gateway connect work {MessageType} for attempt {Attempt} while in {State} state; current attempt is {CurrentAttempt}.",
            connectWork.GetType().Name,
            connectWork.Attempt,
            behaviorName,
            _connectAttempt);

        if (_pendingConnectReplyTo is null)
            return;

        if (connectWork is IMattermostGatewayConnectFailure failure)
            FailPendingConnect(failure.Exception);
    }

    private void IgnoreWrongBehaviorStopWork(IMattermostGatewayStopWork stopWork, string behaviorName)
    {
        _logger.LogDebug(
            "Ignoring Mattermost gateway stop work {MessageType} while in {State} state.",
            stopWork.GetType().Name,
            behaviorName);

        if (stopWork is IMattermostGatewayStopFailure failure)
        {
            stopWork.ReplyTo.Tell(new Status.Failure(failure.Exception));
            return;
        }

        stopWork.ReplyTo.Tell(CurrentSnapshot());
    }

    private void StoreRetryCredentials(string serverUrl, string botToken)
    {
        _retryServerUrl = serverUrl;
        _retryBotToken = botToken;
    }

    private void ScheduleRetryIfEnabled()
    {
        if (!_autoReconnect || _retryServerUrl is null || _retryBotToken is null)
            return;

        CancelRetryTimer();
        _retryTimer = Context.System.Scheduler.ScheduleTellOnceCancelable(
            _retryDelay, Self, RetryConnect.Instance, ActorRefs.NoSender);

        _retryDelay = _retryDelay == TimeSpan.Zero
            ? InitialRetryDelay
            : TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
    }

    private void CancelRetryTimer()
    {
        _retryTimer?.Cancel();
        _retryTimer = null;
    }

    private void CancelAutoReconnect()
    {
        _autoReconnect = false;
        _retryDelay = TimeSpan.Zero;
        CancelRetryTimer();
    }

    private MattermostGatewaySnapshot CurrentSnapshot()
    {
        var isReady = IsReadyCore();
        var healthDetail = isReady
            ? null
            : _healthDetail ?? (_transport.IsConnected
                ? "Mattermost gateway connected but not ready."
                : DisconnectedDetail);

        return new MattermostGatewaySnapshot(
            IsConnected: _transport.IsConnected,
            IsReady: isReady,
            HealthDetail: healthDetail,
            BotUserId: _botUserId,
            BotUsername: _botUsername);
    }

    private bool IsReadyCore() =>
        _isReadyBehavior
        && _botUserId is not null
        && !string.IsNullOrWhiteSpace(_botUsername)
        && _transport.IsConnected;

    private void CompletePendingConnect(MattermostGatewaySnapshot snapshot)
    {
        var replyTo = _pendingConnectReplyTo;
        _pendingConnectReplyTo = null;
        replyTo?.Tell(snapshot);
    }

    private void FailPendingConnect(Exception exception)
    {
        var replyTo = _pendingConnectReplyTo;
        _pendingConnectReplyTo = null;
        replyTo?.Tell(new Status.Failure(exception));
    }

    private void Dispatch(string operation, Func<Task> dispatch)
    {
        Task dispatchTask;
        try
        {
            dispatchTask = dispatch();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {Operation}", operation);
            return;
        }

        if (dispatchTask.IsCompletedSuccessfully)
            return;

        var self = Self;
        dispatchTask.ContinueWith(
            task =>
            {
                if (!task.IsCompletedSuccessfully)
                    self.Tell(new DispatchFailed(operation, UnwrapTaskException(task)));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleDispatchFailed(DispatchFailed failed) =>
        _logger.LogError(failed.Exception, "Error handling {Operation}", failed.Operation);

    private static string BuildDisconnectDetail(MattermostGatewayDisconnect disconnect)
    {
        var reason = disconnect.Exception?.Message ?? disconnect.Reason;
        return string.IsNullOrWhiteSpace(reason)
            ? DisconnectedDetail
            : "Mattermost gateway disconnected: " + reason;
    }

    private static string EndSentence(string message) =>
        message.EndsWith(".", StringComparison.Ordinal) ? message : message + ".";

    private static Exception UnwrapTaskException(Task task)
    {
        if (task.IsCanceled)
            return new TaskCanceledException(task);

        return task.Exception?.GetBaseException()
               ?? new InvalidOperationException("Mattermost gateway operation failed without an exception.");
    }

    internal sealed record Connect(string ServerUrl, string BotToken);

    internal sealed record GetSnapshot
    {
        public static readonly GetSnapshot Instance = new();
    }

    internal sealed record Disconnect
    {
        public static readonly Disconnect Instance = new();
    }

    private interface IMattermostGatewayInternalMessage;

    private interface IMattermostGatewayConnectWork : IMattermostGatewayInternalMessage
    {
        long Attempt { get; }
    }

    private interface IMattermostGatewayConnectFailure : IMattermostGatewayConnectWork
    {
        Exception Exception { get; }
    }

    private interface IMattermostGatewayStopWork : IMattermostGatewayInternalMessage
    {
        IActorRef ReplyTo { get; }
    }

    private interface IMattermostGatewayStopFailure : IMattermostGatewayStopWork
    {
        Exception Exception { get; }
    }

    private sealed record MattermostStartSucceeded(long Attempt, MattermostBotIdentity Identity) : IMattermostGatewayConnectWork;

    private sealed record MattermostStartFailed(long Attempt, Exception Exception) : IMattermostGatewayConnectFailure;

    private sealed record MattermostStopSucceeded(IActorRef ReplyTo) : IMattermostGatewayStopWork;

    private sealed record MattermostStopFailed(IActorRef ReplyTo, Exception Exception) : IMattermostGatewayStopFailure;

    private sealed record MattermostConnected : IMattermostGatewayInternalMessage
    {
        public static readonly MattermostConnected Instance = new();
    }

    private sealed record MattermostDisconnected(MattermostGatewayDisconnect Disconnect) : IMattermostGatewayInternalMessage;

    private sealed record MattermostLogReceived(string Message) : IMattermostGatewayInternalMessage;

    private sealed record MattermostMessageReceived(MattermostGatewayMessage Message) : IMattermostGatewayInternalMessage;

    private sealed record DispatchFailed(string Operation, Exception Exception) : IMattermostGatewayInternalMessage;

    private sealed record RetryConnect : IMattermostGatewayInternalMessage
    {
        public static readonly RetryConnect Instance = new();
    }
}
