// -----------------------------------------------------------------------
// <copyright file="ForwardActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Channels;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class ForwardActor : ReceiveActor
{
    public ForwardActor(IActorRef target)
    {
        Receive<SessionBindingDelivery>(delivery => target.Tell(delivery.Message, delivery.ReplyTo));
        ReceiveAny(msg => target.Tell(msg));
    }
}
