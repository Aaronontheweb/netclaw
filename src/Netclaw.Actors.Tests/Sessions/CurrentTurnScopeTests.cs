// -----------------------------------------------------------------------
// <copyright file="CurrentTurnScopeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Characterization tests for the turn correlation-identity derivation that
/// <see cref="CurrentTurnScope"/> took over from the actor's former
/// <c>BindTurnTelemetry</c> overloads. The fallback chain (source turn id →
/// source message id → generated id) is the only real logic in the scope; these
/// lock it before the actor's ~40 read sites are rewired onto the container.
/// </summary>
public sealed class CurrentTurnScopeTests
{
    private static MessageSource Source(string? messageId, TurnId? turnId, ChannelType channelType = ChannelType.Slack)
        => new()
        {
            ChannelType = channelType,
            SenderId = new SenderId("U123"),
            MessageId = messageId,
            TurnId = turnId,
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Principal = PrincipalClassification.TrustedInternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community)
        };

    [Fact]
    public void Bind_from_source_uses_the_source_turn_id_when_present()
    {
        var scope = new CurrentTurnScope();

        scope.Bind(Source(messageId: "msg-1", turnId: new TurnId("turn-1"), channelType: ChannelType.Discord));

        Assert.Equal("turn-1", scope.TurnId?.Value);
        Assert.Equal("msg-1", scope.MessageId);
        Assert.Equal(ChannelType.Discord, scope.ChannelType);
    }

    [Fact]
    public void Bind_from_source_falls_back_to_message_id_when_no_turn_id()
    {
        var scope = new CurrentTurnScope();

        scope.Bind(Source(messageId: "msg-2", turnId: null));

        Assert.Equal("msg-2", scope.TurnId?.Value);
        Assert.Equal("msg-2", scope.MessageId);
    }

    [Fact]
    public void Bind_from_source_generates_a_turn_id_when_neither_is_present()
    {
        var scope = new CurrentTurnScope();

        scope.Bind(Source(messageId: null, turnId: null));

        Assert.False(string.IsNullOrWhiteSpace(scope.TurnId?.Value));
        Assert.Null(scope.MessageId);
    }

    [Fact]
    public void Bind_from_null_source_still_yields_a_generated_turn_id()
    {
        var scope = new CurrentTurnScope();

        scope.Bind((MessageSource?)null);

        Assert.False(string.IsNullOrWhiteSpace(scope.TurnId?.Value));
        Assert.Null(scope.MessageId);
        Assert.Null(scope.ChannelType);
    }

    [Fact]
    public void Bind_from_turn_context_takes_id_and_channel_and_clears_message_id()
    {
        var scope = new CurrentTurnScope();
        // A prior source bind leaves a message id behind; the context re-bind must clear it.
        scope.Bind(Source(messageId: "stale", turnId: new TurnId("old")));

        var context = TurnContext.FromMessageSource(
            new SessionId("C1/1"),
            new TurnId("turn-ctx"),
            Source(messageId: "ignored", turnId: new TurnId("ignored"), channelType: ChannelType.Mattermost));

        scope.Bind(context);

        Assert.Equal("turn-ctx", scope.TurnId?.Value);
        Assert.Equal(ChannelType.Mattermost, scope.ChannelType);
        Assert.Null(scope.MessageId);
    }
}
