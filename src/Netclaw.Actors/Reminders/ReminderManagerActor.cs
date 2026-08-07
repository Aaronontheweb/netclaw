// -----------------------------------------------------------------------
// <copyright file="ReminderManagerActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Akka.Actor;
using Akka.Event;
using Akka.Reminders;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Singleton actor that mediates between Akka.Reminders and reminder execution.
/// Schedules durable timer entries and resolves execution behavior from
/// file-backed reminder definitions.
/// </summary>
public sealed partial class ReminderManagerActor : ReceiveActor
{
    public const string ShardRegionName = "netclaw-reminders";
    public const string EntityId = "manager";

    /// <summary>
    /// Maximum number of concurrent reminder executions. Not configurable —
    /// if we ever need to tune this, add a knob then.
    /// </summary>
    internal const int MaxConcurrentExecutions = 3;

    /// <summary>
    /// Consecutive execution failures after which a reminder is auto-paused.
    /// Not configurable. Must stay strictly below Akka.Reminders'
    /// <c>MaxDeliveryAttempts</c> (default 10) so Netclaw's auto-pause fires
    /// first — the two counters are kept out of conflict by inspection.
    /// </summary>
    internal const int FailurePauseThreshold = 5;

    /// <summary>Recent run records returned by the per-reminder status query.</summary>
    internal const int RecentHistoryCount = 5;

    private readonly ISessionPipeline _pipeline;
    private readonly EffectivePolicyDefaults _defaults;
    private readonly SchedulingConfig _schedulingConfig;
    private readonly TimeProvider _timeProvider;
    private readonly ReminderDefinitionStore _definitionStore;
    private readonly ReminderHistoryStore _historyStore;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly IReminderChannelNotifier _channelNotifier;
    private readonly ILoggingAdapter _log;

    private IReminderClient? _client;

    private readonly ActiveExecutionTracker _activeExecutions = new();
    private readonly Queue<DeferredReminderOccurrence> _deferredQueue = new();
    private readonly Dictionary<ReminderId, int> _skipCounts = [];

    public ReminderManagerActor(
        ISessionPipeline pipeline,
        EffectivePolicyDefaults defaults,
        SchedulingConfig schedulingConfig,
        TimeProvider timeProvider,
        ReminderDefinitionStore definitionStore,
        ReminderHistoryStore historyStore,
        IOperationalNotificationSink notificationSink,
        IReminderChannelNotifier channelNotifier)
    {
        _pipeline = pipeline;
        _defaults = defaults;
        _schedulingConfig = schedulingConfig;
        _timeProvider = timeProvider;
        _definitionStore = definitionStore;
        _historyStore = historyStore;
        _notificationSink = notificationSink;
        _channelNotifier = channelNotifier;
        _log = Context.GetLogger();

        ReceiveAsync<SaveReminderCommand>(HandleSaveAsync);
        ReceiveAsync<CancelReminderCommand>(HandleCancelAsync);
        ReceiveAsync<DeleteReminderCommand>(HandlePermanentDeleteAsync);
        ReceiveAsync<DisableReminderCommand>(HandleDisableAsync);
        ReceiveAsync<EnableReminderCommand>(HandleEnableAsync);
        ReceiveAsync<ListRemindersCommand>(HandleListAsync);
        ReceiveAsync<GetReminderCommand>(HandleGetAsync);

        ReceiveAsync<ReminderEnvelope<ReminderPayload>>(HandleReminderFiredAsync);
        ReceiveAsync<ReminderExecutionCompleted>(HandleExecutionCompletedAsync);
        ReceiveAsync<ReminderExecutionTerminated>(HandleExecutionTerminatedAsync);

        ReceiveAsync<ReconcileReminders>(_ => HandleReconcileAsync());
        Receive<GetReminderHealthQuery>(_ => HandleGetHealth());
        ReceiveAsync<GetReminderStatusQuery>(HandleGetStatusAsync);
    }

    protected override void PreStart()
    {
        var extension = ReminderClientExtension.Get(Context.System);
        _client = extension.CreateClient(new ReminderEntity(ShardRegionName, EntityId));
        _log.Info("ReminderManagerActor started (scheduling enabled={0})", _schedulingConfig.Enabled);

        if (!_schedulingConfig.Enabled)
        {
            _log.Info("Scheduling is disabled — skipping reminder reconciliation and execution");
            return;
        }

        // The store can be constructed before all persisted files are present.
        // Rescan at the actor's startup boundary so schema alerts reflect the
        // authoritative on-disk state rather than constructor timing.
        _definitionStore.List();
        EmitDroppedInvalidDefinitionAlerts();
        EmitRejectedLegacyDefinitionAlerts();

        Self.Tell(ReconcileReminders.Instance);
    }

    protected override SupervisorStrategy SupervisorStrategy() =>
        new OneForOneStrategy(_ => Directive.Stop);

    private void EmitDroppedInvalidDefinitionAlerts()
    {
        var dropped = _definitionStore.ConsumeDroppedInvalidDefinitions();
        if (dropped.Count == 0)
            return;

        var droppedIds = string.Join(", ", dropped.Select(x => x.ReminderId));
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "reminder.schema.invalid_dropped",
            AlertType.ReminderSchemaDropped,
            $"Dropped {dropped.Count} invalid reminder definition(s) during startup. Re-create reminder IDs: {droppedIds}.",
            AlertSeverity.Warning,
            source: "startup",
            context: new Dictionary<string, string>
            {
                ["droppedCount"] = dropped.Count.ToString(),
                ["droppedIds"] = droppedIds
            }));

        _log.Warning("Dropped {0} invalid reminder definition(s) during startup: {1}", dropped.Count, droppedIds);
    }

    private void EmitRejectedLegacyDefinitionAlerts()
    {
        var rejected = _definitionStore.ConsumeRejectedLegacyDefinitions();
        if (rejected.Count == 0)
            return;

        var rejectedIds = string.Join(", ", rejected.Select(x => x.ReminderId));
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "reminder.schema.legacy_rejected",
            AlertType.ReminderSchemaDropped,
            $"Rejected {rejected.Count} legacy reminder definition(s) missing trust fields during startup. Repair or recreate reminder IDs: {rejectedIds}.",
            AlertSeverity.Warning,
            source: "startup",
            context: new Dictionary<string, string>
            {
                ["rejectedCount"] = rejected.Count.ToString(),
                ["rejectedIds"] = rejectedIds
            }));

        _log.Warning(
            "Rejected {0} legacy reminder definition(s) missing trust fields during startup: {1}",
            rejected.Count,
            rejectedIds);
    }

    private async Task HandleSaveAsync(SaveReminderCommand cmd)
    {
        var replyTo = Sender;

        static ReminderSavedResponse ValidationFailure(ReminderId id, string title, string message)
            => new(
                id,
                title,
                Success: false,
                NextFire: null,
                Error: ReminderSaveError.Validation,
                ErrorMessage: message);

        if (cmd.Definition is null)
        {
            replyTo.Tell(new ReminderSavedResponse(
                new ReminderId("unknown"),
                "unknown",
                Success: false,
                NextFire: null,
                Error: ReminderSaveError.Validation,
                ErrorMessage: "Reminder definition is required."));
            return;
        }

        var title = cmd.Definition.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            replyTo.Tell(new ReminderSavedResponse(
                cmd.Definition.Id,
                string.Empty,
                Success: false,
                NextFire: null,
                Error: ReminderSaveError.Validation,
                ErrorMessage: "Reminder title is required."));
            return;
        }

        var id = !string.IsNullOrWhiteSpace(cmd.Definition.Id.Value)
            ? cmd.Definition.Id
            : ReminderIdGenerator.Generate(title);

        var exists = _definitionStore.Exists(id);

        switch (cmd.WriteMode)
        {
            case ReminderWriteMode.CreateOnly when exists:
                replyTo.Tell(new ReminderSavedResponse(
                    id,
                    title,
                    Success: false,
                    NextFire: null,
                    Error: ReminderSaveError.Conflict,
                    ErrorMessage: $"Reminder '{id.Value}' already exists."));
                return;

            case ReminderWriteMode.Replace when !exists:
                replyTo.Tell(new ReminderSavedResponse(
                    id,
                    title,
                    Success: false,
                    NextFire: null,
                    Error: ReminderSaveError.NotFound,
                    ErrorMessage: $"Reminder '{id.Value}' was not found."));
                return;
        }

        var now = _timeProvider.GetUtcNow();
        var authorization = ValidateRequestedAudience(cmd.Definition.Audience, cmd.Authorization);
        if (!authorization.IsSuccess)
        {
            replyTo.Tell(ValidationFailure(id, title, authorization.ErrorMessage!));
            return;
        }

        // Non-null on the success path — IsSuccess was checked above, and a
        // successful ReminderAudienceAuthorizationResult always carries an audience.
        var effectiveAudience = authorization.EffectiveAudience!.Value;
        var boundaryValidation = ValidateRequestedBoundary(cmd.Definition.Boundary, effectiveAudience);
        if (!boundaryValidation.IsSuccess)
        {
            replyTo.Tell(ValidationFailure(id, title, boundaryValidation.ErrorMessage!));
            return;
        }

        var effectiveBoundary = boundaryValidation.NormalizedBoundary!.Value;

        var normalized = cmd.Definition with
        {
            Id = id,
            Title = title,
            Audience = effectiveAudience,
            Boundary = effectiveBoundary,
            CreatedBy = string.IsNullOrWhiteSpace(cmd.Definition.CreatedBy)
                ? "system"
                : cmd.Definition.CreatedBy
        };

        if (normalized.Schedule.Type == ReminderScheduleType.OneShot && normalized.ExpiresAt is not null)
        {
            replyTo.Tell(ValidationFailure(id, title, "expires_at is not applicable to one-shot reminders."));
            return;
        }

        if (exists)
        {
            var existing = _definitionStore.Get(id);
            normalized.CreatedAtMs = existing?.CreatedAtMs ?? (normalized.CreatedAtMs > 0 ? normalized.CreatedAtMs : now.ToUnixTimeMilliseconds());
        }
        else
        {
            normalized.CreatedAtMs = normalized.CreatedAtMs > 0 ? normalized.CreatedAtMs : now.ToUnixTimeMilliseconds();
        }

        normalized.UpdatedAtMs = now.ToUnixTimeMilliseconds();

        if (exists)
        {
            await CancelScheduleOnlyAsync(id);
            RemoveFromDeferredQueue(id);
        }

        DateTimeOffset? nextFire = null;
        if (normalized.Enabled)
        {
            var scheduleResult = await ScheduleDefinitionAsync(
                normalized,
                rescheduleFromNow: exists || cmd.WriteMode is not ReminderWriteMode.CreateOnly);

            if (!scheduleResult.IsSuccess)
            {
                replyTo.Tell(new ReminderSavedResponse(
                    id,
                    normalized.Title,
                    Success: false,
                    NextFire: null,
                    Error: ReminderSaveError.Validation,
                    ErrorMessage: scheduleResult.ErrorMessage));
                return;
            }

            nextFire = scheduleResult.NextFire;

            // Persist the (possibly rescheduled) interval first-fire time so a daemon
            // restart re-uses the same anchor instead of resetting "now + interval".
            if (normalized.Schedule.Type == ReminderScheduleType.Interval && nextFire is not null)
            {
                normalized = normalized with
                {
                    Schedule = normalized.Schedule with { FireAt = nextFire }
                };
            }
        }
        else
        {
            await CancelScheduleOnlyAsync(id);
            RemoveFromDeferredQueue(id);
        }

        _definitionStore.Save(normalized);

        _log.Info("Saved reminder '{0}' (enabled={1})", normalized.Id, normalized.Enabled);

        replyTo.Tell(new ReminderSavedResponse(
            id,
            normalized.Title,
            Success: true,
            NextFire: nextFire));
    }

    private static ReminderAudienceAuthorizationResult ValidateRequestedAudience(
        TrustAudience requestedAudience,
        ReminderAudienceAuthorizationContext? authorization)
    {
        if (authorization?.SourceAudience is not { } sourceAudience)
        {
            return ReminderAudienceAuthorizationResult.Fail(
                "Reminder audience authorization context is required.");
        }

        var effectiveAudience = requestedAudience;
        if (effectiveAudience > sourceAudience)
        {
            var sourceDescription = string.IsNullOrWhiteSpace(authorization.SourceDescription)
                ? sourceAudience.ToWireValue()
                : authorization.SourceDescription;

            return ReminderAudienceAuthorizationResult.Fail(
                $"Requested audience '{effectiveAudience.ToWireValue()}' exceeds creator authority '{sourceDescription}' ({sourceAudience.ToWireValue()}).");
        }

        return ReminderAudienceAuthorizationResult.Success(effectiveAudience);
    }

    private static ReminderBoundaryValidationResult ValidateRequestedBoundary(
        TrustBoundary requestedBoundary,
        TrustAudience effectiveAudience)
    {
        if (!SecurityPolicyDefaults.TryNormalizeBoundary(requestedBoundary.Value, out var normalizedBoundary))
        {
            return ReminderBoundaryValidationResult.Fail(
                $"Reminder boundary '{requestedBoundary}' is not a recognized trust boundary.");
        }

        if (!SecurityPolicyDefaults.IsBoundaryCompatibleWithAudience(normalizedBoundary, effectiveAudience))
        {
            return ReminderBoundaryValidationResult.Fail(
                $"Reminder boundary '{normalizedBoundary}' is not allowed for audience '{effectiveAudience.ToWireValue()}'.");
        }

        return ReminderBoundaryValidationResult.Success(normalizedBoundary);
    }

    private async Task HandleCancelAsync(CancelReminderCommand cmd)
    {
        var replyTo = Sender;
        var response = await DisableReminderInternalAsync(cmd.Id);

        _log.Info("Cancel reminder '{0}': {1}", cmd.Id.Value, response.Found ? "disabled" : "not found");
        replyTo.Tell(new ReminderCancelledResponse(cmd.Id, response.Found));
    }

    private async Task HandlePermanentDeleteAsync(DeleteReminderCommand cmd)
    {
        var replyTo = Sender;
        var found = _definitionStore.Exists(cmd.Id);
        await DeleteReminderInternalAsync(cmd.Id);

        _log.Info("Permanently delete reminder '{0}': {1}", cmd.Id.Value, found ? "deleted" : "not found");
        replyTo.Tell(new ReminderDeletedResponse(cmd.Id, found));
    }

    private async Task HandleDisableAsync(DisableReminderCommand cmd)
    {
        var replyTo = Sender;
        var response = await DisableReminderInternalAsync(cmd.Id);
        replyTo.Tell(response);
    }

    private async Task HandleEnableAsync(EnableReminderCommand cmd)
    {
        var replyTo = Sender;
        var response = await EnableReminderInternalAsync(cmd.Id);
        replyTo.Tell(response);
    }

    private async Task<ReminderStateResponse> DisableReminderInternalAsync(ReminderId id)
    {
        var definition = _definitionStore.Get(id);
        if (definition is null)
            return new ReminderStateResponse(id, Found: false, Enabled: false, ErrorMessage: "Reminder not found.");

        if (!definition.Enabled)
            return new ReminderStateResponse(id, Found: true, Enabled: false);

        definition = definition with
        {
            Enabled = false,
            UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        _definitionStore.Save(definition);
        await CancelScheduleOnlyAsync(id);

        _skipCounts.Remove(id);
        RemoveFromDeferredQueue(id);

        _log.Info("Disabled reminder '{0}'", id.Value);
        return new ReminderStateResponse(id, Found: true, Enabled: false);
    }

    /// <summary>
    /// Permanently removes a reminder definition, its schedule, history, and process state.
    /// Only an explicit delete command uses this path.
    /// </summary>
    private async Task DeleteReminderInternalAsync(ReminderId id)
    {
        _definitionStore.Delete(id);
        await CancelScheduleOnlyAsync(id);
        _skipCounts.Remove(id);
        RemoveFromDeferredQueue(id);

        try
        {
            _historyStore.DeleteHistory(id);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to delete history for reminder '{0}'", id.Value);
        }
    }

    private async Task<ReminderStateResponse> EnableReminderInternalAsync(ReminderId id)
    {
        var definition = _definitionStore.Get(id);
        if (definition is null)
            return new ReminderStateResponse(id, Found: false, Enabled: false, ErrorMessage: "Reminder not found.");

        definition = definition with
        {
            Enabled = true,
            ConsecutiveFailures = 0,
            TerminalOutcome = null,
            UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        var scheduleResult = await ScheduleDefinitionAsync(definition, rescheduleFromNow: true);
        if (!scheduleResult.IsSuccess)
        {
            definition = definition with
            {
                Enabled = false,
                UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            };
            _definitionStore.Save(definition);

            return new ReminderStateResponse(
                id,
                Found: true,
                Enabled: false,
                ErrorMessage: scheduleResult.ErrorMessage);
        }

        _definitionStore.Save(definition);
        _log.Info("Enabled reminder '{0}'", id.Value);

        return new ReminderStateResponse(
            id,
            Found: true,
            Enabled: true,
            NextFire: scheduleResult.NextFire);
    }

    private async Task HandleListAsync(ListRemindersCommand cmd)
    {
        var replyTo = Sender;
        try
        {
            var definitions = _definitionStore.List();
            var schedules = await ListScheduledRemindersAsync();

            var infos = definitions
                .Where(d => cmd.IncludeDisabled || d.Enabled)
                .OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
                .Select(d => ToReminderInfo(d, schedules.GetValueOrDefault(d.Id.Value)))
                .ToList();

            replyTo.Tell(new ReminderListResponse(infos));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error listing reminders");
            replyTo.Tell(new ReminderListResponse([]));
        }
    }

    private async Task HandleGetAsync(GetReminderCommand cmd)
    {
        var replyTo = Sender;
        try
        {
            var definition = _definitionStore.Get(cmd.Id);
            if (definition is null)
            {
                replyTo.Tell(new GetReminderResponse(null));
                return;
            }

            var schedules = await ListScheduledRemindersAsync();
            var info = ToReminderInfo(definition, schedules.GetValueOrDefault(definition.Id.Value));

            replyTo.Tell(new GetReminderResponse(info));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error getting reminder '{0}'", cmd.Id.Value);
            replyTo.Tell(new GetReminderResponse(null));
        }
    }

    private async Task HandleReminderFiredAsync(ReminderEnvelope<ReminderPayload> envelope)
    {
        if (!_schedulingConfig.Enabled)
        {
            _log.Warning("Scheduling is disabled — ignoring fired reminder and acking envelope");
            await _client!.AckAsync(envelope);
            return;
        }

        var payload = envelope.Message;
        var reminderId = payload.Id;
        var definition = _definitionStore.Get(reminderId);

        if (definition is null)
        {
            _log.Error("Reminder fired for missing definition '{0}'. Cancelling orphaned schedule.", reminderId.Value);
            await CancelScheduleOnlyAsync(reminderId);
            await _client!.AckAsync(envelope);
            return;
        }

        if (!definition.Enabled)
        {
            _log.Warning("Reminder '{0}' fired while disabled. Cancelling any lingering schedule.", reminderId.Value);
            await CancelScheduleOnlyAsync(reminderId);
            RemoveFromDeferredQueue(reminderId);
            await _client!.AckAsync(envelope);
            return;
        }

        if (definition.Schedule.Type is not ReminderScheduleType.OneShot
            && definition.ExpiresAt is { } expiresAt
            && expiresAt <= _timeProvider.GetUtcNow())
        {
            _log.Info("Reminder '{0}' has expired (expiresAt={1}), disabling", reminderId.Value, expiresAt);
            await DisableReminderInternalAsync(reminderId);
            await _client!.AckAsync(envelope);
            return;
        }

        _log.Info("Reminder fired: id='{0}', title='{1}', schedule_type={2}",
            reminderId.Value, definition.Title, definition.Schedule.Type);

        if (_activeExecutions.IsExecuting(reminderId))
        {
            RecordSkippedDuplicate(reminderId, definition.Title, "scheduled");
            EnqueueDeferredOccurrence(envelope);
            return;
        }

        // Cron reminders are implemented as recurring single-shot schedules.
        if (definition.Schedule.Type == ReminderScheduleType.Cron)
        {
            var scheduleResult = await ScheduleDefinitionAsync(definition, rescheduleFromNow: true);
            if (!scheduleResult.IsSuccess)
            {
                _log.Warning("Failed to reschedule cron reminder '{0}': {1}", reminderId.Value, scheduleResult.ErrorMessage);
            }
        }

        if (_activeExecutions.Count >= MaxConcurrentExecutions)
        {
            _log.Info("Concurrency limit reached ({0}), deferring reminder '{1}'",
                MaxConcurrentExecutions, reminderId.Value);
            EnqueueDeferredOccurrence(envelope);
            return;
        }

        // Issue #1803: every delivery mode retains its envelope until the
        // execution actor confirms success or reports a known failure.
        StartExecution(definition, envelope);
    }

    private void EnqueueDeferredOccurrence(ReminderEnvelope<ReminderPayload> envelope)
    {
        if (_deferredQueue.Any(item =>
                item.Envelope.Key == envelope.Key
                && item.Envelope.DueTimeUtc == envelope.DueTimeUtc))
        {
            return;
        }

        _deferredQueue.Enqueue(new DeferredReminderOccurrence(envelope.Message.Id, envelope));
    }

    /// <summary>
    /// Records an occurrence that waits while the same reminder runs.
    /// The status command exposes this in-memory overlap count.
    /// </summary>
    private void RecordSkippedDuplicate(ReminderId reminderId, string title, string source)
    {
        var count = _skipCounts.GetValueOrDefault(reminderId) + 1;
        _skipCounts[reminderId] = count;
        _log.Warning(
            "reminder_skipped_duplicate_execution reminder_id={0} title={1} source={2} skip_count={3}",
            reminderId.Value, title, source, count);
    }

    /// <summary>
    /// Posts an operator-facing failure notice to a reminder's destination
    /// channel. Only Channel-delivery reminders have such a channel; CurrentSession
    /// and None failures are surfaced via the operational alert sink instead. The
    /// notifier is fire-and-forget — never blocks or throws into the manager.
    /// </summary>
    private void PostFailureNoticeToChannel(ReminderDefinition? definition, string text)
    {
        if (definition is not { Delivery.Kind: DeliveryKind.Channel })
            return;

        var target = ReminderExecutionActor.ResolveChannelDeliveryTarget(definition);
        if (target is null)
        {
            _log.Warning(
                "Reminder '{0}' failed but its channel delivery target could not be resolved; no channel notice posted.",
                definition.Id.Value);
            return;
        }

        _channelNotifier.NotifyFailure(target, text);
    }

    private async Task HandleExecutionCompletedAsync(ReminderExecutionCompleted completed)
    {
        if (!_activeExecutions.TryRemove(completed.Id, completed.ExecutionId, out _))
            return;

        await ApplyExecutionResultAsync(completed);
    }

    private async Task HandleExecutionTerminatedAsync(ReminderExecutionTerminated terminated)
    {
        if (!_activeExecutions.TryRemove(terminated.Id, terminated.ExecutionId, out var execution))
            return;

        const string reason = "Reminder execution actor terminated unexpectedly.";
        var nack = await _client!.NackAsync(execution.Envelope, reason);
        var terminal = nack.ResponseCode is ReminderNackResponseCode.Failed
            or ReminderNackResponseCode.Expired;

        await ApplyExecutionResultAsync(new ReminderExecutionCompleted(
            terminated.ExecutionId,
            terminated.Id,
            Success: false,
            ErrorMessage: reason,
            OccurrenceTerminal: terminal));
    }

    private async Task ApplyExecutionResultAsync(ReminderExecutionCompleted completed)
    {
        var definition = _definitionStore.Get(completed.Id);
        var title = definition?.Title ?? completed.Id.Value;

        if (completed.Success)
        {
            if (definition is not null)
            {
                definition = definition with
                {
                    ConsecutiveFailures = 0,
                    Enabled = definition.Schedule.Type is ReminderScheduleType.OneShot
                        ? false
                        : definition.Enabled,
                    TerminalOutcome = definition.Schedule.Type is ReminderScheduleType.OneShot
                        ? ReminderTerminalOutcome.Completed
                        : null,
                    UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                };
                _definitionStore.Save(definition);
            }

            _log.Info("Reminder '{0}' execution completed successfully", completed.Id.Value);
        }
        else
        {
            var count = (definition?.ConsecutiveFailures ?? 0) + 1;
            if (definition is not null)
            {
                definition = definition with
                {
                    ConsecutiveFailures = count,
                    UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                };
                _definitionStore.Save(definition);
            }

            _log.Warning("Reminder '{0}' execution failed ({1}/{2}): {3}",
                completed.Id.Value,
                count,
                FailurePauseThreshold,
                completed.ErrorMessage);

            _notificationSink.Emit(OperationalAlert.Create(
                _timeProvider,
                "reminder.execution.failed",
                AlertType.ReminderExecutionFailed,
                $"Reminder '{title}' execution failed: {completed.ErrorMessage}",
                AlertSeverity.Warning,
                source: completed.Id.Value,
                context: new Dictionary<string, string>
                {
                    ["reminderId"] = completed.Id.Value,
                    ["title"] = title,
                    ["error"] = completed.ErrorMessage ?? "unknown",
                }));

            // Surface the failure where the operator expects this reminder's
            // output: its destination channel. Only below the threshold — on the
            // threshold-hitting failure the disabled notice below already carries
            // the last error, so posting both would double the noise on the most
            // important event. Bounded overall by the threshold, never the
            // unbounded skip stream that #1494 makes visible via status instead.
            if (count < FailurePauseThreshold)
            {
                PostFailureNoticeToChannel(
                    definition,
                    $"Reminder \"{title}\" failed: {completed.ErrorMessage ?? "unknown error"}");
            }

            if (count >= FailurePauseThreshold || completed.OccurrenceTerminal)
            {
                var disableReason = count >= FailurePauseThreshold
                    ? $"failure threshold ({FailurePauseThreshold})"
                    : "the occurrence retry budget";
                _log.Warning("Reminder '{0}' hit {1}, disabling",
                    completed.Id.Value,
                    disableReason);

                _notificationSink.Emit(OperationalAlert.Create(
                    _timeProvider,
                    "reminder.auto_disabled",
                    AlertType.ReminderAutoDisabled,
                    $"Reminder '{title}' disabled after {count} consecutive failures",
                    AlertSeverity.Critical,
                    source: completed.Id.Value,
                    context: new Dictionary<string, string>
                    {
                        ["reminderId"] = completed.Id.Value,
                        ["title"] = title,
                        ["failureCount"] = count.ToString(),
                    }));

                PostFailureNoticeToChannel(
                    definition,
                    $"Reminder \"{title}\" was automatically disabled after {count} consecutive failures. " +
                    $"Last error: {completed.ErrorMessage ?? "unknown error"}");

                if (definition is not null)
                {
                    definition = definition with
                    {
                        Enabled = false,
                        TerminalOutcome = ReminderTerminalOutcome.Failed,
                        UpdatedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                    };
                    _definitionStore.Save(definition);
                }

                await CancelScheduleOnlyAsync(completed.Id);
                RemoveFromDeferredQueue(completed.Id);
            }
        }

        await ProcessDeferredQueueAsync();
    }

    private async Task HandleReconcileAsync()
    {
        var sender = Sender; // capture before any await
        try
        {
            var scheduled = await ListScheduledRemindersAsync();
            var definitions = _definitionStore.List();
            var definitionsById = definitions.ToDictionary(d => d.Id.Value, StringComparer.Ordinal);

            var cancelledOrphans = 0;
            foreach (var (id, _) in scheduled)
            {
                if (!definitionsById.TryGetValue(id, out var definition) || !definition.Enabled)
                {
                    await CancelScheduleOnlyAsync(new ReminderId(id));
                    cancelledOrphans++;
                }
            }

            var restoredSchedules = 0;
            foreach (var definition in definitions.Where(d => d.Enabled))
            {
                if (scheduled.ContainsKey(definition.Id.Value))
                    continue;

                if (definition.Schedule.Type == ReminderScheduleType.OneShot
                    && definition.Schedule.FireAt <= _timeProvider.GetUtcNow())
                {
                    continue;
                }

                var result = await ScheduleDefinitionAsync(definition, rescheduleFromNow: true);
                if (result.IsSuccess)
                    restoredSchedules++;
            }

            // Issue #1803: a past due time and the absence of a schedule do not prove success.
            var now = _timeProvider.GetUtcNow();
            var softDeletedOneShots = 0;
            foreach (var definition in definitions.Where(d =>
                         d.Enabled &&
                         d.Schedule.Type == ReminderScheduleType.OneShot &&
                         d.Schedule.FireAt <= now))
            {
                var occurrence = await GetOccurrenceStatusAsync(definition);
                if (occurrence is null)
                {
                    _log.Warning(
                        "Past one-shot reminder '{0}' has no durable occurrence status. The definition remains enabled for operator review.",
                        definition.Id.Value);
                    continue;
                }

                var outcome = occurrence.CompletionStatus switch
                {
                    Akka.Reminders.Storage.ReminderCompletionStatus.Delivered => ReminderTerminalOutcome.Completed,
                    Akka.Reminders.Storage.ReminderCompletionStatus.Failed => ReminderTerminalOutcome.Failed,
                    Akka.Reminders.Storage.ReminderCompletionStatus.Expired => ReminderTerminalOutcome.Failed,
                    Akka.Reminders.Storage.ReminderCompletionStatus.Cancelled => ReminderTerminalOutcome.Failed,
                    _ => (ReminderTerminalOutcome?)null
                };

                if (outcome is null)
                    continue;

                var terminalDefinition = definition with
                {
                    Enabled = false,
                    TerminalOutcome = outcome,
                    UpdatedAtMs = now.ToUnixTimeMilliseconds()
                };
                _definitionStore.Save(terminalDefinition);
                softDeletedOneShots++;
            }

            // Disable expired recurring reminders that haven't fired since expiration.
            var disabledExpired = 0;
            foreach (var definition in definitions.Where(d =>
                         d.Enabled &&
                         d.Schedule.Type is not ReminderScheduleType.OneShot &&
                         d.ExpiresAt is { } ea && ea <= now))
            {
                await DisableReminderInternalAsync(definition.Id);
                disabledExpired++;
            }

            if (cancelledOrphans > 0 || restoredSchedules > 0 || softDeletedOneShots > 0 || disabledExpired > 0)
            {
                _log.Info("Reminder reconcile complete: cancelled_orphans={0}, restored={1}, soft_deleted_oneshots={2}, disabled_expired={3}",
                    cancelledOrphans,
                    restoredSchedules,
                    softDeletedOneShots,
                    disabledExpired);
            }

            // Only ack external callers — skip Self.Tell from PreStart
            if (!sender.Equals(Self))
                sender.Tell(new ReconcileCompleted(cancelledOrphans, restoredSchedules, softDeletedOneShots, disabledExpired));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Reminder reconcile failed");

            if (!sender.Equals(Self))
                sender.Tell(new Status.Failure(ex));
        }
    }

    private void StartExecution(
        ReminderDefinition definition,
        ReminderEnvelope<ReminderPayload> envelope)
    {
        var executionId = Guid.NewGuid();
        _activeExecutions.Add(definition.Id, executionId, envelope);

        var actorName = $"exec-{SanitizeActorName(definition.Id.Value)}-{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}";
        var executionActor = Context.ActorOf(
            ReminderExecutionActor.CreateProps(
                executionId,
                definition,
                _pipeline,
                _timeProvider,
                _historyStore,
                envelope),
            actorName);
        Context.WatchWith(
            executionActor,
            new ReminderExecutionTerminated(executionId, definition.Id));

        _log.Info(
            "Started execution actor for reminder '{0}' occurrence={1}: {2}",
            definition.Id, envelope.DueTimeUtc, executionActor.Path);
    }

    private async Task ProcessDeferredQueueAsync()
    {
        var candidates = _deferredQueue.Count;
        while (_deferredQueue.Count > 0
               && _activeExecutions.Count < MaxConcurrentExecutions
               && candidates-- > 0)
        {
            var deferred = _deferredQueue.Dequeue();
            var nextId = deferred.Id;
            var definition = _definitionStore.Get(nextId);
            if (definition is null || !definition.Enabled)
            {
                await _client!.AckAsync(deferred.Envelope);
                continue;
            }

            if (_activeExecutions.IsExecuting(nextId))
            {
                EnqueueDeferredOccurrence(deferred.Envelope);
                continue;
            }

            var now = _timeProvider.GetUtcNow();
            if (definition.Schedule.Type is not ReminderScheduleType.OneShot
                && definition.ExpiresAt is { } expiresAt
                && expiresAt <= now)
            {
                _log.Info("Deferred reminder '{0}' expired while queued (expiresAt={1}), disabling", nextId.Value, expiresAt);
                await DisableReminderInternalAsync(nextId);
                continue;
            }

            StartExecution(definition, deferred.Envelope);
        }
    }

    private void RemoveFromDeferredQueue(ReminderId id)
    {
        if (_deferredQueue.Count == 0)
            return;

        var keep = new Queue<DeferredReminderOccurrence>();
        while (_deferredQueue.Count > 0)
        {
            var item = _deferredQueue.Dequeue();
            if (item.Id != id)
                keep.Enqueue(item);
        }

        while (keep.Count > 0)
            _deferredQueue.Enqueue(keep.Dequeue());
    }

    private async Task<ScheduleAttempt> ScheduleDefinitionAsync(ReminderDefinition definition, bool rescheduleFromNow)
    {
        if (_client is null)
            return ScheduleAttempt.Fail("Reminder client is not initialized.");

        var id = definition.Id;
        var key = new ReminderKey(definition.Id.Value);
        var payload = new ReminderPayload { Id = id };
        var now = _timeProvider.GetUtcNow();

        try
        {
            switch (definition.Schedule.Type)
            {
                case ReminderScheduleType.OneShot:
                {
                    if (definition.Schedule.FireAt is null)
                        return ScheduleAttempt.Fail("One-shot reminders require an absolute fire time.");

                    var fireAt = definition.Schedule.FireAt.Value;
                    if (fireAt <= now)
                        return ScheduleAttempt.Fail("One-shot fire time is in the past.");

                    var result = await _client.ScheduleSingleReminderAsync(key, fireAt, payload);
                    return result.ResponseCode == ReminderScheduleResponseCode.Success
                        ? ScheduleAttempt.Ok(fireAt)
                        : ScheduleAttempt.Fail(result.Message ?? "Failed to schedule one-shot reminder.");
                }

                case ReminderScheduleType.Interval:
                {
                    if (definition.Schedule.Interval is null)
                        return ScheduleAttempt.Fail("Interval reminders require an interval duration.");

                    var interval = definition.Schedule.Interval.Value;
                    var first = rescheduleFromNow
                        ? now.Add(interval)
                        : definition.Schedule.FireAt is { } explicitFirst && explicitFirst > now
                            ? explicitFirst
                            : now.Add(interval);

                    var result = await _client.ScheduleRecurringReminderAsync(key, first, interval, payload);
                    return result.ResponseCode == ReminderScheduleResponseCode.Success
                        ? ScheduleAttempt.Ok(first)
                        : ScheduleAttempt.Fail(result.Message ?? "Failed to schedule interval reminder.");
                }

                case ReminderScheduleType.Cron:
                {
                    if (string.IsNullOrWhiteSpace(definition.Schedule.CronExpression))
                        return ScheduleAttempt.Fail("Cron reminders require a cron expression.");

                    var nextFire = CronScheduleHelper.GetNextOccurrence(definition.Schedule.CronExpression, _timeProvider);
                    if (nextFire is null)
                        return ScheduleAttempt.Fail("Cron schedule has no future occurrence.");

                    var result = await _client.ScheduleSingleReminderAsync(key, nextFire.Value, payload);
                    return result.ResponseCode == ReminderScheduleResponseCode.Success
                        ? ScheduleAttempt.Ok(nextFire)
                        : ScheduleAttempt.Fail(result.Message ?? "Failed to schedule cron reminder.");
                }

                default:
                    return ScheduleAttempt.Fail("Unknown schedule type.");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error scheduling reminder '{0}'", definition.Id);
            return ScheduleAttempt.Fail(ex.Message);
        }
    }

    private async Task<Dictionary<string, DateTimeOffset?>> ListScheduledRemindersAsync()
    {
        var map = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);

        if (_client is null)
            return map;

        var result = await _client.ListRemindersAsync();
        if (result.ResponseCode != FetchRemindersResponseCode.Success)
            return map;

        foreach (var scheduled in result.Reminders)
        {
            if (scheduled.Message is ReminderPayload payload)
                map[payload.Id.Value] = scheduled.When;
            // Ignore unknown payload types
        }

        return map;
    }

    private async Task<bool> CancelScheduleOnlyAsync(ReminderId id)
    {
        if (_client is null)
            return false;

        try
        {
            var result = await _client.CancelReminderAsync(new ReminderKey(id.Value));
            return result.ResponseCode == ReminderCancelResponseCode.Success;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error cancelling reminder schedule '{0}'", id.Value);
            return false;
        }
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]", RegexOptions.Compiled)]
    private static partial Regex InvalidActorNameChars();

    private static ReminderInfo ToReminderInfo(ReminderDefinition d, DateTimeOffset? nextFire) => new(
        Id: d.Id,
        Title: d.Title,
        Instructions: d.Instructions,
        Delivery: d.Delivery,
        DeliveryRequired: d.DeliveryRequired,
        DeliveryInstructions: d.DeliveryInstructions,
        Schedule: d.Schedule,
        NextFire: nextFire,
        Enabled: d.Enabled,
        AgentDefinitionId: d.AgentDefinitionId,
        Audience: d.Audience,
        ExpiresAt: d.ExpiresAt,
        ConsecutiveFailures: d.ConsecutiveFailures,
        TerminalOutcome: d.TerminalOutcome);

    private static string SanitizeActorName(string raw)
    {
        var sanitized = InvalidActorNameChars().Replace(raw, "-");
        if (string.IsNullOrWhiteSpace(sanitized))
            return "reminder";
        if (sanitized.Length > 60)
            return sanitized[..60];
        return sanitized;
    }

    private void HandleGetHealth()
    {
        var scheduledCount = _definitionStore.List().Count(d => d.Enabled);
        Sender.Tell(new ReminderHealthResponse(
            scheduledCount,
            _activeExecutions.Count,
            _definitionStore.List().Count(d => d.ConsecutiveFailures > 0)));
    }

    private async Task HandleGetStatusAsync(GetReminderStatusQuery query)
    {
        var replyTo = Sender;
        try
        {
            var definition = _definitionStore.Get(query.Id);
            if (definition is null)
            {
                replyTo.Tell(new ReminderStatusResponse(
                    query.Id, Found: false, Enabled: false, Executing: false,
                    NextFire: null, ConsecutiveFailures: 0, SkippedDuplicates: 0,
                    TerminalOutcome: null, Occurrence: null,
                    RecentHistory: []));
                return;
            }

            // Two independent backend reads — run them concurrently so the
            // query's latency is max(schedule, history) instead of their sum.
            // Neither touches actor state until both complete.
            var schedulesTask = ListScheduledRemindersAsync();
            var historyTask = _historyStore.ReadAsync(query.Id, RecentHistoryCount);
            var occurrenceTask = GetOccurrenceStatusAsync(definition);
            await Task.WhenAll(schedulesTask, historyTask, occurrenceTask);

            var occurrence = occurrenceTask.Result is { } status
                ? new ReminderOccurrenceInfo(
                    status.DueTimeUtc,
                    status.NextAttemptAtUtc,
                    status.AttemptCount,
                    status.LastFailureReason,
                    status.CompletionStatus.ToString(),
                    status.DeliveryDeadlineUtc,
                    status.AckDeadlineUtc,
                    status.CompletedAtUtc)
                : null;

            replyTo.Tell(new ReminderStatusResponse(
                query.Id,
                Found: true,
                Enabled: definition.Enabled,
                Executing: _activeExecutions.IsExecuting(query.Id),
                NextFire: schedulesTask.Result.GetValueOrDefault(query.Id.Value),
                ConsecutiveFailures: definition.ConsecutiveFailures,
                SkippedDuplicates: _skipCounts.GetValueOrDefault(query.Id),
                TerminalOutcome: definition.TerminalOutcome,
                Occurrence: occurrence,
                RecentHistory: historyTask.Result));
        }
        catch (Exception ex)
        {
            // The definition existed, so this is a transient read failure — NOT a
            // missing reminder. Faulting the Ask surfaces a real error (the
            // endpoint maps it to 5xx); replying not-found here would tell the
            // operator a wedged reminder was deleted, the silent fallback this
            // very feature exists to expose.
            _log.Error(ex, "Error getting status for reminder '{0}'", query.Id.Value);
            replyTo.Tell(new Status.Failure(ex));
        }
    }

    private async Task<ReminderOccurrenceStatus?> GetOccurrenceStatusAsync(ReminderDefinition definition)
    {
        if (_client is null
            || definition.Schedule.Type is not ReminderScheduleType.OneShot
            || definition.Schedule.FireAt is not { } dueTimeUtc)
        {
            return null;
        }

        var response = await _client.GetOccurrenceStatusAsync(
            new ReminderKey(definition.Id.Value),
            dueTimeUtc);

        return response.ResponseCode switch
        {
            ReminderOccurrenceStatusResponseCode.Success => response.Status,
            ReminderOccurrenceStatusResponseCode.NotFound => null,
            ReminderOccurrenceStatusResponseCode.Error => throw new InvalidOperationException(
                response.Message ?? "The reminder occurrence status query failed."),
            _ => throw new InvalidOperationException(
                $"Unexpected occurrence status response: {response.ResponseCode}.")
        };
    }

    private sealed record ScheduleAttempt(bool IsSuccess, DateTimeOffset? NextFire, string? ErrorMessage) : INoSerializationVerificationNeeded
    {
        public static ScheduleAttempt Ok(DateTimeOffset? nextFire) => new(true, nextFire, null);
        public static ScheduleAttempt Fail(string message) => new(false, null, message);
    }

    private sealed record DeferredReminderOccurrence(
        ReminderId Id,
        ReminderEnvelope<ReminderPayload> Envelope) : INoSerializationVerificationNeeded;

    private sealed record ReminderAudienceAuthorizationResult(bool IsSuccess, TrustAudience? EffectiveAudience, string? ErrorMessage) : INoSerializationVerificationNeeded
    {
        public static ReminderAudienceAuthorizationResult Success(TrustAudience effectiveAudience)
            => new(true, effectiveAudience, null);

        public static ReminderAudienceAuthorizationResult Fail(string errorMessage)
            => new(false, null, errorMessage);
    }

    private sealed record ReminderBoundaryValidationResult(bool IsSuccess, TrustBoundary? NormalizedBoundary, string? ErrorMessage) : INoSerializationVerificationNeeded
    {
        public static ReminderBoundaryValidationResult Success(TrustBoundary normalizedBoundary)
            => new(true, normalizedBoundary, null);

        public static ReminderBoundaryValidationResult Fail(string errorMessage)
            => new(false, null, errorMessage);
    }

    internal sealed record ReconcileReminders : INoSerializationVerificationNeeded
    {
        public static readonly ReconcileReminders Instance = new();
    }

    /// <summary>
    /// Ack sent back to <see cref="ReconcileReminders"/> callers so they can
    /// synchronize on reconcile completion instead of polling.
    /// </summary>
    internal sealed record ReconcileCompleted(
        int CancelledOrphans,
        int RestoredSchedules,
        int SoftDeletedOneShots,
        int DisabledExpired = 0) : INoSerializationVerificationNeeded;
}
