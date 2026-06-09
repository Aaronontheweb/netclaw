// -----------------------------------------------------------------------
// <copyright file="IChannel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;

namespace Netclaw.Channels;

/// <summary>
/// Marker interface for input/output channels. Each channel is a hosted service
/// that manages one or more sessions through Akka.Streams pipelines.
/// </summary>
public interface IChannel : IHostedService
{
    Actors.Channels.ChannelType ChannelType { get; }

    string DisplayName { get; }

    ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default);
}

public enum ChannelHealthStatus
{
    Healthy,
    Degraded,
    Disconnected
}

public sealed record ChannelHealth(ChannelHealthStatus Status, string? Detail = null);

/// <summary>
/// Common health surface shared by channel gateway transport snapshots
/// (Discord, Mattermost). Channel-specific snapshot records carry their own
/// bot identity fields on top of this contract.
/// </summary>
public interface IGatewaySnapshot
{
    bool IsConnected { get; }

    bool IsReady { get; }

    string? HealthDetail { get; }
}

/// <summary>
/// Shared <see cref="ChannelHealth"/> evaluation for channels whose transport
/// exposes an <see cref="IGatewaySnapshot"/>. Fallback strings are
/// caller-supplied so each channel keeps its exact operator-facing wording.
/// </summary>
public static class GatewayChannelHealth
{
    public static ChannelHealth Evaluate(
        IGatewaySnapshot snapshot,
        string? connectFailureDetail,
        string notReadyFallback,
        string disconnectedFallback)
    {
        if (snapshot.IsReady)
            return new ChannelHealth(ChannelHealthStatus.Healthy);

        if (snapshot.IsConnected)
            return new ChannelHealth(
                ChannelHealthStatus.Degraded,
                snapshot.HealthDetail ?? connectFailureDetail ?? notReadyFallback);

        return new ChannelHealth(
            ChannelHealthStatus.Disconnected,
            connectFailureDetail ?? snapshot.HealthDetail ?? disconnectedFallback);
    }
}
