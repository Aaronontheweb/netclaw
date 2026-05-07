// -----------------------------------------------------------------------
// <copyright file="LoggingRegistrationExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Hosting;
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

        // Construct as a DI-resolved singleton so the provider can lazily resolve
        // IRequiredActor<SessionLogDispatcherActorKey> after the actor system boots.
        builder.Logging.Services.AddSingleton<ILoggerProvider>(sp =>
        {
            var timeProvider = sp.GetService<TimeProvider>();
            Func<Task<Akka.Actor.IActorRef>>? dispatcherFactory = () =>
            {
                var requiredActor = sp.GetRequiredService<IRequiredActor<SessionLogDispatcherActorKey>>();
                return requiredActor.GetAsync();
            };
            return new RollingFileLoggerProvider(
                resolvedPaths.DaemonLogPath,
                dispatcherFactory,
                timeProvider);
        });

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
