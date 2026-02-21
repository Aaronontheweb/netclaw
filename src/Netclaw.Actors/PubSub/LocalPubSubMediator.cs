using Akka.Actor;
using Akka.Event;

namespace Netclaw.Actors.PubSub;

/// <summary>
/// In-memory pub/sub mediator actor for single-process execution.
/// Manages string-keyed topic subscriptions and uses DeathWatch to
/// automatically clean up terminated subscribers.
///
/// A future clustered implementation would delegate to
/// Akka.Cluster.Tools DistributedPubSub.
/// </summary>
public sealed class LocalPubSubMediator : ReceiveActor
{
    private readonly Dictionary<string, HashSet<IActorRef>> _topicSubscribers = new();
    private readonly ILoggingAdapter _log = Context.GetLogger();

    public LocalPubSubMediator()
    {
        Receive<Subscribe>(OnSubscribe);
        Receive<Unsubscribe>(OnUnsubscribe);
        Receive<Publish>(OnPublish);
    }

    private void OnSubscribe(Subscribe cmd)
    {
        if (!_topicSubscribers.TryGetValue(cmd.Topic, out var subscribers))
        {
            subscribers = new HashSet<IActorRef>();
            _topicSubscribers[cmd.Topic] = subscribers;
        }

        if (subscribers.Add(cmd.Subscriber))
        {
            Context.WatchWith(cmd.Subscriber, new Unsubscribe(cmd.Topic, cmd.Subscriber));
            _log.Debug("Subscribed {0} to topic [{1}]", cmd.Subscriber, cmd.Topic);
        }
    }

    private void OnUnsubscribe(Unsubscribe cmd)
    {
        if (!_topicSubscribers.TryGetValue(cmd.Topic, out var subscribers))
            return;

        if (subscribers.Remove(cmd.Subscriber))
        {
            if (subscribers.Count == 0)
                _topicSubscribers.Remove(cmd.Topic);

            _log.Debug("Unsubscribed {0} from topic [{1}]", cmd.Subscriber, cmd.Topic);
        }
    }

    private void OnPublish(Publish cmd)
    {
        if (!_topicSubscribers.TryGetValue(cmd.Topic, out var subscribers))
            return;

        foreach (var subscriber in subscribers)
        {
            subscriber.Tell(cmd.Message);
        }
    }

    public static Props CreateProps() => Props.Create<LocalPubSubMediator>();
}
