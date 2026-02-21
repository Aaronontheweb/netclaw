using Akka.Actor;

namespace Netclaw.Actors.PubSub;

/// <summary>
/// Transport-agnostic pub/sub mediator for string-keyed topic routing.
/// Adapters subscribe to session topics to receive broadcasts (TurnBroadcast,
/// CompactionBroadcast). Session actors publish to their topic after each turn.
///
/// Local implementation uses in-memory routing. A future clustered implementation
/// would delegate to Akka.Cluster.Tools DistributedPubSub.
/// </summary>
public interface IPubSubMediator
{
    void Publish(string topic, object message);
    void Subscribe(string topic, IActorRef subscriber);
    void Unsubscribe(string topic, IActorRef subscriber);
}
