// -----------------------------------------------------------------------
// <copyright file="LoggingRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

public static class LoggingRegistrationExtensions
{
    public static LogLevel ConfigureNetclawLogging(this WebApplicationBuilder builder, NetclawPaths? paths = null)
    {
        var level = ResolveLogLevel(builder.Configuration);
        var consoleEnabled = builder.Configuration.GetValue("Logging:Console:Enabled", false);
        var resolvedPaths = paths ?? new NetclawPaths();

        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        if (consoleEnabled)
            builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

        // Always write to a rolling log file in ~/.netclaw/logs/
        Directory.CreateDirectory(resolvedPaths.LogsDirectory);
        builder.Logging.AddProvider(new RollingFileLoggerProvider(
            resolvedPaths.DaemonLogPath,
            resolvedPaths.SessionLogsDirectory));

        builder.Logging.SetMinimumLevel(level);
        return level;
    }

    private static LogLevel ResolveLogLevel(IConfiguration configuration)
    {
        var configured = configuration["Logging:LogLevel:Default"];
        if (Enum.TryParse<LogLevel>(configured, ignoreCase: true, out var level))
            return level;

        return LogLevel.Information;
    }
}
