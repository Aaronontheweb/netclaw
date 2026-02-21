using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.PubSub;
using Netclaw.Actors.Routing;
using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Hosting;

public static class NetclawAkkaHostingExtensions
{
    /// <summary>
    /// Registers the <see cref="LocalPubSubMediator"/> in the actor registry.
    /// </summary>
    public static AkkaConfigurationBuilder WithPubSubMediator(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, _) =>
        {
            var pubSub = system.ActorOf(
                LocalPubSubMediator.CreateProps(), "pub-sub-mediator");
            registry.Register<PubSubMediatorActor>(pubSub);
        });
    }

    /// <summary>
    /// Registers the session manager as a <see cref="GenericChildPerEntityParent"/>
    /// that routes <see cref="Protocol.IWithSessionId"/> messages to per-session
    /// <see cref="LlmSessionActor"/> children.
    /// Requires <see cref="WithPubSubMediator"/> to be called first.
    /// </summary>
    public static AkkaConfigurationBuilder WithSessionManager(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var sessionManager = system.ActorOf(
                GenericChildPerEntityParent.CreateProps(
                    new SessionMessageExtractor(),
                    entityId => resolver.Props<LlmSessionActor>(entityId)),
                "session-manager");
            registry.Register<SessionManagerActor>(sessionManager);
        });
    }

    /// <summary>
    /// Convenience method that registers all Netclaw actor infrastructure.
    /// Equivalent to calling <see cref="WithPubSubMediator"/> and
    /// <see cref="WithSessionManager"/> in sequence.
    /// </summary>
    public static AkkaConfigurationBuilder WithNetclawActors(
        this AkkaConfigurationBuilder builder)
    {
        return builder
            .WithPubSubMediator()
            .WithSessionManager();
    }
}
