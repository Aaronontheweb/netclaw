using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.PubSub;
using Netclaw.Actors.Routing;
using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Hosting;

public static class NetclawAkkaHostingExtensions
{
    /// <summary>
    /// Registers the <see cref="LocalPubSubMediator"/> and the session manager
    /// as a <see cref="GenericChildPerEntityParent"/> that routes
    /// <see cref="Protocol.IWithSessionId"/> messages to per-session
    /// <see cref="LlmSessionActor"/> children.
    /// </summary>
    public static AkkaConfigurationBuilder WithNetclawActors(
        this AkkaConfigurationBuilder builder)
    {
        return builder.StartActors((system, registry, resolver) =>
        {
            var pubSub = system.ActorOf(
                LocalPubSubMediator.CreateProps(), "pub-sub-mediator");
            registry.Register<PubSubMediatorActor>(pubSub);

            var sessionManager = system.ActorOf(
                GenericChildPerEntityParent.CreateProps(
                    new SessionMessageExtractor(),
                    entityId => Props.Create(() => new LlmSessionActor(entityId, pubSub))),
                "session-manager");
            registry.Register<SessionManagerActor>(sessionManager);
        });
    }
}
