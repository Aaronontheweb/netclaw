// -----------------------------------------------------------------------
// <copyright file="ISessionHubClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// SignalR client contract for session output.
/// The daemon sends this message by name because Native AOT does not support typed hubs.
/// </summary>
public interface ISessionHubClient
{
    Task ReceiveOutput(SessionOutputDto output);
}
