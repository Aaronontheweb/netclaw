using System.Runtime.CompilerServices;

namespace Netclaw.Actors.Tests;

internal static class TestEnvironmentInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Disable config file watching in test hosts.
        // Akka.Hosting.TestKit spins up real IHost instances which enable
        // file watchers for appsettings.json reload by default. Running many
        // tests exhausts inotify watch limits on Linux.
        Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
    }
}
