using Akka.Actor;
using Akka.Cluster.Sharding;

namespace Netclaw.Actors.Hosting;

/// <summary>
/// A generic "child per entity" parent actor that re-uses Akka.Cluster.Sharding's
/// <see cref="IMessageExtractor"/> for routing without requiring cluster sharding.
/// </summary>
/// <remarks>
/// Creates child actors on-demand keyed by entity ID. Same protocol works with both
/// this parent (local/test) and ShardRegion (clustered).
/// </remarks>
public sealed class GenericChildPerEntityParent : ReceiveActor
{
    public static Props CreateProps(IMessageExtractor extractor, Func<string, Props> propsFactory)
    {
        return Props.Create(() => new GenericChildPerEntityParent(extractor, propsFactory));
    }

    private readonly IMessageExtractor _extractor;
    private readonly Func<string, Props> _propsFactory;

    public GenericChildPerEntityParent(IMessageExtractor extractor, Func<string, Props> propsFactory)
    {
        _extractor = extractor;
        _propsFactory = propsFactory;

        ReceiveAny(o =>
        {
            var result = _extractor.EntityId(o);
            if (result is null) return;
            Context.Child(result)
                .GetOrElse(() => Context.ActorOf(_propsFactory(result), result))
                .Forward(_extractor.EntityMessage(o));
        });
    }
}
