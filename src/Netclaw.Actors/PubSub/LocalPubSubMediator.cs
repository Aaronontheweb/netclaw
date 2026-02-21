using System.Collections.Concurrent;
using Akka.Actor;

namespace Netclaw.Actors.PubSub;

/// <summary>
/// In-memory pub/sub mediator for single-process execution.
/// Thread-safe — multiple actors can publish/subscribe concurrently.
/// </summary>
public sealed class LocalPubSubMediator : IPubSubMediator
{
    private readonly ConcurrentDictionary<string, HashSet<IActorRef>> _subscriptions = new();

    public void Publish(string topic, object message)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(message);

        if (!_subscriptions.TryGetValue(topic, out var subscribers))
            return;

        IActorRef[] snapshot;
        lock (subscribers)
        {
            snapshot = [.. subscribers];
        }

        foreach (var subscriber in snapshot)
        {
            subscriber.Tell(message);
        }
    }

    public void Subscribe(string topic, IActorRef subscriber)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(subscriber);

        var subscribers = _subscriptions.GetOrAdd(topic, _ => new HashSet<IActorRef>());
        lock (subscribers)
        {
            subscribers.Add(subscriber);
        }
    }

    public void Unsubscribe(string topic, IActorRef subscriber)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(subscriber);

        if (!_subscriptions.TryGetValue(topic, out var subscribers))
            return;

        lock (subscribers)
        {
            subscribers.Remove(subscriber);
        }
    }
}
