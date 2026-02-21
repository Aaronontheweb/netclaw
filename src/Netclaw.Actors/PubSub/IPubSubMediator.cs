using Akka.Actor;

namespace Netclaw.Actors.PubSub;

/// <summary>
/// Subscribe to a string-keyed topic. The mediator will DeathWatch
/// the subscriber and automatically unsubscribe on termination.
/// </summary>
public sealed record Subscribe(string Topic, IActorRef Subscriber);

/// <summary>
/// Unsubscribe from a string-keyed topic.
/// </summary>
public sealed record Unsubscribe(string Topic, IActorRef Subscriber);

/// <summary>
/// Publish a message to all subscribers of a topic.
/// </summary>
public sealed record Publish(string Topic, object Message);
