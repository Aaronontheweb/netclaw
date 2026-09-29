// -----------------------------------------------------------------------
// <copyright file="DaemonConfigurationRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Daemon configuration sources and the daemon <see cref="ToolConfig"/> registration.
/// Program.cs and the daemon tests use these methods, so both read the same runtime shape.
/// </summary>
public static class DaemonConfigurationRegistrationExtensions
{
    /// <summary>
    /// Adds the daemon configuration sources. A later source overrides an earlier source:
    /// netclaw.json, then secrets.json, then <c>NETCLAW_*</c> environment variables.
    /// </summary>
    public static IConfigurationBuilder AddNetclawDaemonSources(this IConfigurationBuilder builder, NetclawPaths paths)
        => builder
            .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
            .AddJsonFile(paths.SecretsPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("NETCLAW_");

    /// <summary>
    /// Binds the <c>Tools</c> section with list replacement and registers the result.
    /// Binding warnings go to the startup log when the host starts.
    /// </summary>
    public static ToolConfig AddDaemonToolConfig(this IServiceCollection services, IConfiguration configuration)
    {
        var toolConfig = ToolConfig.BindFromConfiguration(configuration.GetSection("Tools"), out var warnings);
        services.AddSingleton(toolConfig);
        if (warnings.Count > 0)
        {
            // Registration runs before the host builds its logging providers. A hosted
            // service logs the warnings through the real providers when the host starts.
            services.AddHostedService(sp => new ConfigurationWarningLogService(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Netclaw.Startup"),
                warnings));
        }

        return toolConfig;
    }
}

internal sealed class ConfigurationWarningLogService(ILogger logger, IReadOnlyList<string> warnings) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var warning in warnings)
            logger.LogWarning("Configuration warning: {ConfigurationWarning}", warning);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
