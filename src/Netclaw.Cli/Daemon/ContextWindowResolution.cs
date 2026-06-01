// -----------------------------------------------------------------------
// <copyright file="ContextWindowResolution.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Resolves the effective context window for the main model by combining
/// the explicit config value with a daemon status query fallback.
/// </summary>
internal static class ContextWindowResolution
{
    public static async Task<ModelRuntimeResolution> ResolveRuntimeAsync(ModelReference configuredMain, DaemonApi daemon)
    {
        DaemonRuntimeStatus.Response? status = null;
        try
        {
            status = await GetStatusAsync(daemon);
        }
        catch (DaemonUnavailableException) when (configuredMain.ContextWindow is > 0)
        {
            return new ModelRuntimeResolution(
                configuredMain.ModelId,
                configuredMain.Provider,
                configuredMain.ContextWindow.Value);
        }

        if (status?.Model is { ContextWindow: > 0 } daemonModel)
        {
            return new ModelRuntimeResolution(
                daemonModel.ModelId,
                daemonModel.Provider,
                daemonModel.ContextWindow);
        }

        if (configuredMain.ContextWindow is > 0)
        {
            return new ModelRuntimeResolution(
                configuredMain.ModelId,
                configuredMain.Provider,
                configuredMain.ContextWindow.Value);
        }

        throw new InvalidOperationException(
            $"Daemon reported no context window for model '{configuredMain.ModelId}'. " +
            "Set Models.Main.ContextWindow in netclaw.json.");
    }

    /// <summary>
    /// Returns the explicit config value when set; otherwise queries the daemon
    /// status endpoint for the auto-detected context window.
    /// </summary>
    public static async Task<int> ResolveAsync(int? configuredContextWindow, DaemonApi daemon, string modelId)
    {
        if (configuredContextWindow is > 0)
            return configuredContextWindow.Value;

        var status = await GetStatusAsync(daemon);

        return status.Model?.ContextWindow is > 0 and var daemonCw
            ? daemonCw
            : throw new InvalidOperationException(
                $"Daemon reported no context window for model '{modelId}'. " +
                "Set Models.Main.ContextWindow in netclaw.json.");
    }

    private static async Task<DaemonRuntimeStatus.Response> GetStatusAsync(DaemonApi daemon)
    {
        try
        {
            return await daemon.GetStatusAsync()
                ?? throw new InvalidOperationException(
                    "Daemon returned empty status. Cannot resolve effective context window. " +
                    "Set Models.Main.ContextWindow in netclaw.json or ensure the daemon is healthy.");
        }
        catch (HttpRequestException ex)
        {
            throw new DaemonUnavailableException(daemon.Endpoint, ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new DaemonUnavailableException(daemon.Endpoint, ex);
        }
    }
}

internal sealed record ModelRuntimeResolution(
    string ModelId,
    string Provider,
    int ContextWindowTokens);

internal sealed class DaemonUnavailableException : InvalidOperationException
{
    public DaemonUnavailableException(string endpoint, Exception innerException)
        : base(
            $"Could not reach the Netclaw daemon at {endpoint}. " +
            "Start it with 'netclaw daemon start' or run 'netclaw doctor' for diagnostics.",
            innerException)
    {
        Endpoint = endpoint;
    }

    public string Endpoint { get; }
}
