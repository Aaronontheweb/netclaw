// -----------------------------------------------------------------------
// <copyright file="ContainerSupervisor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Daemon;

/// <summary>
/// Reports whether an external process supervisor owns the daemon lifecycle.
/// </summary>
/// <remarks>
/// The official Docker image runs <c>entrypoint.sh</c> as PID 1 and supervises
/// <c>netclawd</c>, restarting it on exit. In that environment the CLI must
/// never spawn a detached daemon of its own — doing so creates a second
/// <c>netclawd</c> that races the supervised one for the singleton lock file
/// (#1279). The image declares this by setting
/// <c>NETCLAW_CONTAINER_SUPERVISOR</c>; the CLI keys its behavior off the
/// marker rather than off generic container detection, because the invariant
/// that matters is "an external supervisor owns start/stop," not "we happen to
/// be inside a container."
/// </remarks>
public interface IContainerSupervisor
{
    /// <summary>
    /// <c>true</c> when an external supervisor owns daemon start/stop and the
    /// CLI must defer the lifecycle to it instead of spawning <c>netclawd</c>.
    /// </summary>
    bool IsExternallySupervised { get; }
}

/// <inheritdoc cref="IContainerSupervisor"/>
public sealed class ContainerSupervisor : IContainerSupervisor
{
    public bool IsExternallySupervised =>
        Environment.GetEnvironmentVariable("NETCLAW_CONTAINER_SUPERVISOR") is { Length: > 0 };
}
