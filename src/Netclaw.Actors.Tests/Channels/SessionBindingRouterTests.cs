// -----------------------------------------------------------------------
// <copyright file="SessionBindingRouterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Channels;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SessionBindingRouterTests(ITestOutputHelper output) : TestKit(output: output)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    protected override Config? Config =>
        ConfigurationFactory.ParseString("akka.test.default-timeout = 5s");

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task Retire_barrier_replays_pre_notice_before_buffered_delivery_with_original_reply_targets()
    {
        var parentEvents = CreateTestProbe("parent-events");
        var bindingEvents = CreateTestProbe("binding-events");
        var oldReplyTo = CreateTestProbe("old-reply-to");
        var newReplyTo = CreateTestProbe("new-reply-to");
        var nextGeneration = 0;
        Func<Props> propsFactory = () => Props.Create(() => new ControlledBindingActor(
            () => Interlocked.Increment(ref nextGeneration) - 1,
            bindingEvents.Ref));
        var parent = Sys.ActorOf(Props.Create(() => new RouterHarnessActor(parentEvents.Ref, propsFactory)));

        parent.Tell(new RouteDelivery("old", oldReplyTo.Ref), TestActor);
        var oldRouted = await parentEvents.ExpectMsgAsync<RouteAccepted>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("old", oldRouted.MessageId);
        var oldHeld = await bindingEvents.ExpectMsgAsync<DeliveryHeld>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, oldHeld.Generation);
        Assert.Equal(oldReplyTo.Ref, oldHeld.ReplyTo);

        parent.Tell(BeginRetirement.Instance, TestActor);
        var retiring = await parentEvents.ExpectMsgAsync<RetiringObserved>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(oldHeld.Binding, retiring.Binding);

        parent.Tell(new RouteDelivery("new", newReplyTo.Ref), TestActor);
        var buffered = await parentEvents.ExpectMsgAsync<RouteAccepted>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("new", buffered.MessageId);

        retiring.Binding.Tell(ReleaseBarrier.Instance, TestActor);

        var replayedOld = await bindingEvents.ExpectMsgAsync<DeliveryAccepted>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        var replayedNew = await bindingEvents.ExpectMsgAsync<DeliveryAccepted>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Collection(
            new[] { replayedOld, replayedNew },
            delivery =>
            {
                Assert.Equal(1, delivery.Generation);
                Assert.Equal("old", delivery.MessageId);
                Assert.Equal(oldReplyTo.Ref, delivery.ReplyTo);
            },
            delivery =>
            {
                Assert.Equal(1, delivery.Generation);
                Assert.Equal("new", delivery.MessageId);
                Assert.Equal(newReplyTo.Ref, delivery.ReplyTo);
            });

        await oldReplyTo.ExpectMsgAsync<DeliveryAck>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        await newReplyTo.ExpectMsgAsync<DeliveryAck>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        var removed = await parentEvents.ExpectMsgAsync<BindingRemoved>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, removed.LiveChildren);

        var completionReplyTo = CreateTestProbe("delivery-barrier-reply-to");
        parent.Tell(new RouteDelivery("barrier", completionReplyTo.Ref), TestActor);
        var barrierRouted = await parentEvents.ExpectMsgAsync<RouteAccepted>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("barrier", barrierRouted.MessageId);
        var deliveredBarrier = await bindingEvents.ExpectMsgAsync<DeliveryBarrierObserved>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, deliveredBarrier.Generation);
        await completionReplyTo.ExpectMsgAsync<DeliveryAck>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Empty_retirement_does_not_recreate_binding_until_a_route_arrives()
    {
        var parentEvents = CreateTestProbe("empty-parent-events");
        var bindingEvents = CreateTestProbe("empty-binding-events");
        var nextGeneration = 0;
        Func<Props> propsFactory = () => Props.Create(() => new ControlledBindingActor(
            () => Interlocked.Increment(ref nextGeneration) - 1,
            bindingEvents.Ref));
        var parent = Sys.ActorOf(Props.Create(() => new RouterHarnessActor(parentEvents.Ref, propsFactory)));

        parent.Tell(BeginEmptyRetirement.Instance, TestActor);
        var retiring = await parentEvents.ExpectMsgAsync<RetiringObserved>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        var removed = await parentEvents.ExpectMsgAsync<BindingRemoved>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, removed.LiveChildren);

        parent.Tell(new RouteDelivery("after-empty-retirement", TestActor), TestActor);
        var accepted = await parentEvents.ExpectMsgAsync<RouteAccepted>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("after-empty-retirement", accepted.MessageId);
        var recreated = await bindingEvents.ExpectMsgAsync<DeliveryAccepted>(
            TestTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, recreated.Generation);
        Assert.Equal("after-empty-retirement", recreated.MessageId);
    }

    private sealed class RouterHarnessActor : ReceiveActor
    {
        private const string BindingName = "test-thread";
        private readonly IActorRef _observer;
        private readonly Func<Props> _propsFactory;
        private readonly SessionBindingRouter _router = new();

        public RouterHarnessActor(IActorRef observer, Func<Props> propsFactory)
        {
            _observer = observer;
            _propsFactory = propsFactory;

            Receive<RouteDelivery>(request =>
            {
                _router.Route(Context, BindingName, _propsFactory, request.Message, request.ReplyTo);
                _observer.Tell(new RouteAccepted(request.Message), Self);
            });

            Receive<BeginRetirement>(_ =>
            {
                var child = _router.GetOrCreate(Context, BindingName, _propsFactory);
                child.Tell(BeginRetirement.Instance, Self);
            });

            Receive<BeginEmptyRetirement>(_ =>
            {
                var child = _router.GetOrCreate(Context, BindingName, _propsFactory);
                child.Tell(BeginRetirement.Instance, Self);
            });

            Receive<SessionBindingRetiring>(_ =>
            {
                var child = Sender;
                if (_router.HandleRetiring(Context, child))
                    _observer.Tell(new RetiringObserved(child), Self);
            });

            Receive<SessionBindingDelivery>(delivery =>
                _router.HandleDeferred(Sender, delivery));

            Receive<SessionBindingRetireReady>(_ => _router.HandleReady(Context, Sender));

            Receive<Terminated>(terminated =>
            {
                var droppedCount = _router.HandleTerminated(Context, terminated.ActorRef);
                if (droppedCount is not null)
                    _observer.Tell(new BindingRemoved(terminated.ActorRef, Context.GetChildren().Count()), Self);
            });
        }
    }

    private sealed class ControlledBindingActor : ReceiveActor
    {
        private readonly int _generation;
        private readonly IActorRef _observer;
        private SessionBindingDelivery? _heldDelivery;

        public ControlledBindingActor(Func<int> generationFactory, IActorRef observer)
        {
            _generation = generationFactory();
            _observer = observer;

            Receive<SessionBindingDelivery>(delivery =>
            {
                if (_generation == 0)
                {
                    _heldDelivery = delivery;
                    _observer.Tell(new DeliveryHeld(
                        _generation,
                        MessageId(delivery.Message),
                        delivery.ReplyTo,
                        Self), Self);
                    return;
                }

                var messageId = MessageId(delivery.Message);
                if (messageId == "barrier")
                    _observer.Tell(new DeliveryBarrierObserved(_generation), Self);
                else
                    _observer.Tell(new DeliveryAccepted(_generation, messageId, delivery.ReplyTo), Self);
                delivery.ReplyTo.Tell(new DeliveryAck(messageId), Self);
            });

            Receive<BeginRetirement>(_ =>
                Context.Parent.Tell(SessionBindingRetiring.Instance, Self));

            Receive<SessionBindingRetireBarrier>(_ =>
            {
                if (_heldDelivery is null)
                    Context.Parent.Tell(SessionBindingRetireReady.Instance, Self);
            });

            Receive<ReleaseBarrier>(_ =>
            {
                if (_heldDelivery is null)
                    throw new InvalidOperationException("The old delivery was not held.");

                Context.Parent.Tell(_heldDelivery, Self);
                Context.Parent.Tell(SessionBindingRetireReady.Instance, Self);
            });
        }

        private static string MessageId(object message) => ((RouteMessage)message).MessageId;
    }

    private sealed record RouteMessage(string MessageId);
    private sealed record RouteDelivery(string MessageId, IActorRef ReplyTo)
    {
        public RouteMessage Message { get; } = new(MessageId);
    }

    private sealed record RouteAccepted(RouteMessage Message)
    {
        public string MessageId => Message.MessageId;
    }

    private sealed record DeliveryHeld(
        int Generation,
        string MessageId,
        IActorRef ReplyTo,
        IActorRef Binding);

    private sealed record DeliveryAccepted(int Generation, string MessageId, IActorRef ReplyTo);
    private sealed record DeliveryBarrierObserved(int Generation);
    private sealed record DeliveryAck(string MessageId);
    private sealed record RetiringObserved(IActorRef Binding);
    private sealed record BindingRemoved(IActorRef Binding, int LiveChildren);
    private sealed record BeginEmptyRetirement
    {
        public static readonly BeginEmptyRetirement Instance = new();
    }
    private sealed record BeginRetirement
    {
        public static readonly BeginRetirement Instance = new();
    }
    private sealed record ReleaseBarrier
    {
        public static readonly ReleaseBarrier Instance = new();
    }
}
