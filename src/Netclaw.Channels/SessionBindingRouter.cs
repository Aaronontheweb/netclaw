// -----------------------------------------------------------------------
// <copyright file="SessionBindingRouter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;

namespace Netclaw.Channels;

/// <summary>
/// A local conversation-parent delivery that preserves the original reply target.
/// The message does not cross a process boundary.
/// </summary>
public sealed record SessionBindingDelivery(object Message, IActorRef ReplyTo) : INoSerializationVerificationNeeded;

/// <summary>A local notice that a binding has started graceful retirement.</summary>
public sealed record SessionBindingRetiring : INoSerializationVerificationNeeded
{
    public static readonly SessionBindingRetiring Instance = new();
    private SessionBindingRetiring() { }
}

/// <summary>A local parent barrier that follows all earlier routed deliveries.</summary>
public sealed record SessionBindingRetireBarrier : INoSerializationVerificationNeeded
{
    public static readonly SessionBindingRetireBarrier Instance = new();
    private SessionBindingRetireBarrier() { }
}

/// <summary>A local response that confirms the binding processed its retire barrier.</summary>
public sealed record SessionBindingRetireReady : INoSerializationVerificationNeeded
{
    public static readonly SessionBindingRetireReady Instance = new();
    private SessionBindingRetireReady() { }
}

/// <summary>
/// Owns route state in the conversation actor while binding children retire.
/// The state is actor-local. It does not survive parent failure.
/// It replays messages after a binding completes the graceful retire barrier.
/// </summary>
public sealed class SessionBindingRouter
{
    private readonly Dictionary<string, BindingRoute> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<IActorRef, BindingRoute> _routesByChild = new();

    /// <summary>Returns true when the child exists or its route retires.</summary>
    public bool HasBinding(IActorContext context, string actorName)
        => (_routes.TryGetValue(actorName, out var route) && route.Retiring)
            || !context.Child(actorName).IsNobody();

    /// <summary>Returns the binding child and caches its first props for graceful replacement.</summary>
    public IActorRef GetOrCreate(IActorContext context, string actorName, Func<Props> propsFactory)
    {
        var route = GetRoute(context, actorName, propsFactory);
        if (route.Retiring)
            route.EnsureAfterRetirement = true;

        return route.Child;
    }

    /// <summary>Routes a payload or buffers it while the child retires.</summary>
    public void Route(
        IActorContext context,
        string actorName,
        Func<Props> propsFactory,
        object message,
        IActorRef replyTo)
    {
        var route = GetRoute(context, actorName, propsFactory);
        var delivery = new SessionBindingDelivery(message, replyTo);
        if (route.Retiring)
        {
            route.ArrivalsDuringRetirement.Enqueue(delivery);
            return;
        }

        route.Child.Tell(delivery, context.Self);
    }

    /// <summary>Starts the FIFO barrier after the child announces retirement.</summary>
    public bool HandleRetiring(IActorContext context, IActorRef child)
    {
        if (!_routesByChild.TryGetValue(child, out var route) || route.Retiring)
            return false;

        route.Retiring = true;
        child.Tell(SessionBindingRetireBarrier.Instance, context.Self);
        return true;
    }

    /// <summary>Stores a delivery that the retiring child returned.</summary>
    public bool HandleDeferred(IActorRef child, SessionBindingDelivery delivery)
    {
        if (!_routesByChild.TryGetValue(child, out var route) || !route.Retiring)
            return false;

        route.DeferredBeforeBarrier.Enqueue(delivery);
        return true;
    }

    /// <summary>Stops the child after it processes the barrier.</summary>
    public bool HandleReady(IActorContext context, IActorRef child)
    {
        if (!_routesByChild.TryGetValue(child, out var route) || !route.Retiring || route.Ready)
            return false;

        route.Ready = true;
        context.Stop(child);
        return true;
    }

    /// <summary>Replays buffered deliveries after graceful termination.</summary>
    /// <returns>The dropped delivery count, or null when the child was not tracked.</returns>
    public int? HandleTerminated(IActorContext context, IActorRef child)
    {
        if (!_routesByChild.TryGetValue(child, out var route))
            return null;

        _routesByChild.Remove(child);
        if (!route.Retiring || !route.Ready)
        {
            var droppedCount = route.DeferredBeforeBarrier.Count + route.ArrivalsDuringRetirement.Count;
            _routes.Remove(route.ActorName);
            return droppedCount;
        }

        if (!route.EnsureAfterRetirement
            && route.DeferredBeforeBarrier.Count == 0
            && route.ArrivalsDuringRetirement.Count == 0)
        {
            _routes.Remove(route.ActorName);
            return 0;
        }

        route.Child = Spawn(context, route.ActorName, route.Props);
        route.Retiring = false;
        route.Ready = false;
        route.EnsureAfterRetirement = false;
        _routesByChild.Add(route.Child, route);

        while (route.DeferredBeforeBarrier.TryDequeue(out var deferred))
            route.Child.Tell(deferred, context.Self);

        while (route.ArrivalsDuringRetirement.TryDequeue(out var buffered))
            route.Child.Tell(buffered, context.Self);

        return 0;
    }

    private BindingRoute GetRoute(IActorContext context, string actorName, Func<Props> propsFactory)
    {
        if (_routes.TryGetValue(actorName, out var route))
        {
            if (route.Retiring)
                return route;

            var currentChild = context.Child(actorName);
            if (!currentChild.IsNobody())
            {
                if (!Equals(route.Child, currentChild))
                {
                    _routesByChild.Remove(route.Child);
                    route.Child = currentChild;
                    _routesByChild.Add(currentChild, route);
                }

                return route;
            }

            _routesByChild.Remove(route.Child);
            _routes.Remove(actorName);
        }

        var props = propsFactory();
        var child = Spawn(context, actorName, props);
        route = new BindingRoute(actorName, props, child);
        _routes.Add(actorName, route);
        _routesByChild.Add(child, route);
        return route;
    }

    private static IActorRef Spawn(IActorContext context, string actorName, Props props)
    {
        var child = context.ActorOf(props, actorName);
        context.Watch(child);
        return child;
    }

    private sealed class BindingRoute(string actorName, Props props, IActorRef child)
    {
        public string ActorName { get; } = actorName;
        public Props Props { get; } = props;
        public IActorRef Child { get; set; } = child;
        public bool Retiring { get; set; }
        public bool Ready { get; set; }
        public bool EnsureAfterRetirement { get; set; }
        public Queue<SessionBindingDelivery> DeferredBeforeBarrier { get; } = new();
        public Queue<SessionBindingDelivery> ArrivalsDuringRetirement { get; } = new();
    }
}
