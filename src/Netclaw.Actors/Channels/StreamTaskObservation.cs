// -----------------------------------------------------------------------
// <copyright file="StreamTaskObservation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka;
using Akka.Streams.Dsl;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Helpers for observing the internal <see cref="Task{Done}"/> instances
/// that Akka.Streams stages (e.g. <c>Sink.ForEach</c>'s <c>IgnoreSink</c>
/// and <c>Source.Queue</c>'s <c>_completion</c>) create via
/// <c>TaskCompletionSource</c>. Those tasks are faulted on stream
/// teardown; if nothing observes them, the finalizer surfaces the fault
/// as <see cref="TaskScheduler.UnobservedTaskException"/>. Real failures
/// still surface through <c>WatchTermination</c> / actor messages — this
/// only silences the duplicate finalizer noise.
/// </summary>
internal static class StreamTaskObservation
{
    /// <summary>
    /// Attach a fault-only continuation that reads <see cref="Task.Exception"/>
    /// so the task is marked observed before the finalizer runs.
    /// </summary>
    public static void ObserveSilently(Task task)
    {
        _ = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Replace a sink's <see cref="Task{Done}"/> materialized value with
    /// <see cref="NotUsed"/> after attaching <see cref="ObserveSilently"/>
    /// to the underlying task. Use when the caller wants to discard the
    /// materialized value but the underlying TCS still gets faulted on
    /// teardown.
    /// </summary>
    public static Sink<TIn, NotUsed> ObservingFault<TIn>(this Sink<TIn, Task<Done>> sink) =>
        sink.MapMaterializedValue<NotUsed>(static task =>
        {
            ObserveSilently(task);
            return NotUsed.Instance;
        });
}
