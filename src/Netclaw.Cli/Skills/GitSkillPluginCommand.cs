// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;

namespace Netclaw.Cli.Skills;

internal static class GitSkillPluginCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        DaemonApi? daemonApi,
        TimeProvider timeProvider,
        TextReader input,
        TextWriter output)
    {
        var action = args.Length > 2 ? args[2] : "help";
        if (action is "help" or "-h" or "--help")
            return WriteHelp(output);
        if (daemonApi is null)
        {
            output.WriteLine("Daemon unavailable: the daemon API is not configured.");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return action switch
            {
                "install" => await InstallAsync(args, daemonApi, timeProvider, input, output, cancellation.Token),
                "list" => await ListAsync(daemonApi, output, cancellation.Token),
                "enable" => await SetEnabledAsync(args, true, daemonApi, timeProvider, input, output, cancellation.Token),
                "disable" => await SetEnabledAsync(args, false, daemonApi, timeProvider, input, output, cancellation.Token),
                "remove" => await RemoveAsync(args, daemonApi, timeProvider, input, output, cancellation.Token),
                _ => WriteUnknownAction(action, output),
            };
        }
        catch (HttpRequestException ex)
        {
            output.WriteLine(ex.StatusCode is null
                ? $"Plugin command failed: could not reach the daemon ({ex.Message})."
                : $"Plugin command failed: the daemon returned HTTP {(int)ex.StatusCode}.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("Plugin command canceled.");
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteLine($"Plugin command failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> InstallAsync(
        string[] args,
        DaemonApi api,
        TimeProvider timeProvider,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!TryParseInstall(args, output, out var request, out var confirmed))
            return 1;
        if (!await ConfirmAsync(
                confirmed,
                $"Configure plugin from '{request.Repository}'? [y/N]: ",
                input,
                output,
                cancellationToken))
            return 0;

        var response = await api.InstallGitSkillPluginAsync(request, cancellationToken);
        if (response?.Plugin is null || string.IsNullOrWhiteSpace(response.Plugin.Name))
        {
            output.WriteLine("Plugin install failed: the daemon returned an unreadable result.");
            return 1;
        }

        output.WriteLine($"Configured plugin '{response.Plugin.Name}'.");

        return await ApplyAndVerifyAsync(
            api,
            timeProvider,
            response.RestartGeneration,
            response.Plugin.Name,
            GitSkillPluginApi.PluginStatus.Installed,
            output,
            cancellationToken);
    }

    private static async Task<int> ListAsync(
        DaemonApi api,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var response = await api.ListGitSkillPluginsAsync(cancellationToken);
        if (response?.Plugins is null)
        {
            output.WriteLine("Plugin list failed: the daemon returned an unreadable result.");
            return 1;
        }
        if (response.Plugins.Count == 0)
        {
            output.WriteLine("No managed skill plugins.");
            return 0;
        }

        output.WriteLine($"{"NAME",-24}  {"STATUS",-14}  {"VERSION",-14}  REFERENCE");
        foreach (var plugin in response.Plugins)
        {
            var version = plugin.InstalledVersion ?? "-";
            var reference = plugin.ReferenceKind == GitSkillPluginReferenceKind.Commit
                ? plugin.Reference[..Math.Min(12, plugin.Reference.Length)]
                : plugin.Reference;
            output.WriteLine(
                $"{plugin.Name,-24}  {StatusText(plugin.Status),-14}  {version,-14}  {plugin.ReferenceKind}:{reference}");
        }
        return 0;
    }

    private static async Task<int> SetEnabledAsync(
        string[] args,
        bool enabled,
        DaemonApi api,
        TimeProvider timeProvider,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!TryReadName(args, enabled ? "enable" : "disable", output, out var name, out var confirmed))
            return 1;
        if (!await ConfirmAsync(
                confirmed,
                $"{(enabled ? "Enable" : "Disable")} plugin '{name}'? [y/N]: ",
                input,
                output,
                cancellationToken))
            return 0;

        var response = await api.SetGitSkillPluginEnabledAsync(name, enabled, cancellationToken);
        if (response is null)
        {
            output.WriteLine("Plugin change failed: the daemon returned an unreadable result.");
            return 1;
        }
        if (!response.Changed)
        {
            if (!enabled)
            {
                output.WriteLine($"Plugin '{name}' is already disabled.");
                return 0;
            }

            var plugins = await api.ListGitSkillPluginsAsync(cancellationToken);
            var plugin = plugins?.Plugins.FirstOrDefault(
                item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (plugin?.Status == GitSkillPluginApi.PluginStatus.Installed)
            {
                output.WriteLine($"Plugin '{name}' is already enabled.");
                return 0;
            }

            return await SyncAndVerifyAsync(
                api,
                name,
                GitSkillPluginApi.PluginStatus.Installed,
                output,
                cancellationToken);
        }

        return await ApplyAndVerifyAsync(
            api,
            timeProvider,
            response.RestartGeneration,
            name,
            enabled ? GitSkillPluginApi.PluginStatus.Installed : GitSkillPluginApi.PluginStatus.Disabled,
            output,
            cancellationToken);
    }

    private static async Task<int> RemoveAsync(
        string[] args,
        DaemonApi api,
        TimeProvider timeProvider,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!TryReadName(args, "remove", output, out var name, out var confirmed))
            return 1;
        if (!await ConfirmAsync(
                confirmed,
                $"Remove plugin '{name}'? [y/N]: ",
                input,
                output,
                cancellationToken))
            return 0;

        var response = await api.RemoveGitSkillPluginAsync(name, cancellationToken);
        if (response is null)
        {
            output.WriteLine("Plugin removal failed: the daemon returned an unreadable result.");
            return 1;
        }
        if (!await WaitForRestartAsync(api, timeProvider, response.RestartGeneration, output, cancellationToken))
            return 1;

        var sync = await api.SyncSkillsAsync(cancellationToken);
        if (sync?.Inventory.Succeeded != true)
        {
            output.WriteLine($"Plugin '{name}' was removed, but the skill inventory refresh failed.");
            return 1;
        }

        var plugins = await api.ListGitSkillPluginsAsync(cancellationToken);
        if (plugins?.Plugins.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) != false)
        {
            output.WriteLine($"Plugin '{name}' removal could not be verified.");
            return 1;
        }

        output.WriteLine($"Removed plugin '{name}'.");
        return 0;
    }

    private static async Task<int> ApplyAndVerifyAsync(
        DaemonApi api,
        TimeProvider timeProvider,
        int generation,
        string name,
        GitSkillPluginApi.PluginStatus expectedStatus,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!await WaitForRestartAsync(api, timeProvider, generation, output, cancellationToken))
            return 1;

        return await SyncAndVerifyAsync(
            api,
            name,
            expectedStatus,
            output,
            cancellationToken);
    }

    private static async Task<int> SyncAndVerifyAsync(
        DaemonApi api,
        string name,
        GitSkillPluginApi.PluginStatus expectedStatus,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var sync = await api.SyncSkillsAsync(cancellationToken);
        var source = sync?.Sources.FirstOrDefault(
            item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        var plugins = await api.ListGitSkillPluginsAsync(cancellationToken);
        var plugin = plugins?.Plugins.FirstOrDefault(
            item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (sync?.Inventory.Succeeded != true
            || plugin?.Status != expectedStatus
            || (expectedStatus == GitSkillPluginApi.PluginStatus.Installed
                && (source is null || source.FailedCount > 0 || source.RejectedCount > 0)))
        {
            output.WriteLine($"Plugin '{name}' is configured but is not installed.");
            return 1;
        }

        output.WriteLine(expectedStatus == GitSkillPluginApi.PluginStatus.Disabled
            ? $"Disabled plugin '{name}'."
            : $"Installed plugin '{name}' at commit {plugin.InstalledCommit}.");
        return 0;
    }

    private static async Task<bool> WaitForRestartAsync(
        DaemonApi api,
        TimeProvider timeProvider,
        int generation,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        output.WriteLine("Waiting for the daemon to apply the plugin configuration.");
        var ready = await new DaemonRestartWaiter(api, timeProvider)
            .WaitAsync(generation, cancellationToken);
        if (ready)
            return true;

        output.WriteLine("The plugin configuration was saved, but the daemon did not become ready.");
        return false;
    }

    private static bool TryParseInstall(
        string[] args,
        TextWriter output,
        out GitSkillPluginApi.InstallRequest request,
        out bool confirmed)
    {
        request = null!;
        confirmed = false;
        if (args.Length < 4)
        {
            output.WriteLine("Usage: netclaw skill plugin install <repository> [options]");
            return false;
        }

        var repository = args[3];
        string? name = null;
        string? format = null;
        string? subdirectory = null;
        string? reference = null;
        var referenceKind = GitSkillPluginApi.InstallReferenceKind.DefaultBranch;
        var referenceOptionSeen = false;
        var timeoutSeconds = 60;

        for (var index = 4; index < args.Length; index++)
        {
            var option = args[index];
            if (option is "--yes" or "-y")
            {
                confirmed = true;
                continue;
            }
            if (index + 1 >= args.Length)
            {
                output.WriteLine($"Option '{option}' requires a value.");
                return false;
            }
            var value = args[++index];
            switch (option)
            {
                case "--name": name = value; break;
                case "--format": format = value; break;
                case "--subdirectory": subdirectory = value; break;
                case "--timeout-seconds" when int.TryParse(value, out timeoutSeconds): break;
                case "--timeout-seconds":
                    output.WriteLine("The plugin timeout must be an integer.");
                    return false;
                case "--branch":
                case "--tag":
                case "--commit":
                    if (referenceOptionSeen)
                    {
                        output.WriteLine("Use only one of --branch, --tag, or --commit.");
                        return false;
                    }
                    referenceOptionSeen = true;
                    reference = value;
                    referenceKind = option switch
                    {
                        "--branch" => GitSkillPluginApi.InstallReferenceKind.Branch,
                        "--tag" => GitSkillPluginApi.InstallReferenceKind.Tag,
                        _ => GitSkillPluginApi.InstallReferenceKind.Commit,
                    };
                    break;
                default:
                    output.WriteLine($"Unknown plugin install option '{option}'.");
                    return false;
            }
        }

        if (!GitSkillPluginSourceValidator.TryNormalizeRepository(repository, out _, out var repositoryError))
        {
            output.WriteLine(repositoryError);
            return false;
        }
        if (name is not null && !GitSkillPluginSourceValidator.TryValidateName(name, out var nameError))
        {
            output.WriteLine(nameError);
            return false;
        }
        if (!GitSkillPluginSourceValidator.TryNormalizeRelativePath(
                subdirectory,
                allowEmpty: true,
                out _,
                out var pathError))
        {
            output.WriteLine(pathError);
            return false;
        }
        if (timeoutSeconds is < 1 or > 300)
        {
            output.WriteLine("The plugin timeout must be from 1 through 300 seconds.");
            return false;
        }

        request = new GitSkillPluginApi.InstallRequest
        {
            Repository = repository,
            Name = name,
            Format = format ?? "codex",
            Subdirectory = subdirectory,
            ReferenceKind = referenceKind,
            Reference = reference,
            TimeoutSeconds = timeoutSeconds,
        };
        return true;
    }

    private static bool TryReadName(
        string[] args,
        string action,
        TextWriter output,
        out string name,
        out bool confirmed)
    {
        name = args.Length > 3 ? args[3] : string.Empty;
        confirmed = args.Length == 5 && args[4] is "--yes" or "-y";
        if (args.Length != 4 && !confirmed)
        {
            output.WriteLine($"Usage: netclaw skill plugin {action} <name> [--yes]");
            return false;
        }
        if (!GitSkillPluginSourceValidator.TryValidateName(name, out var error))
        {
            output.WriteLine(error);
            return false;
        }
        return true;
    }

    private static async Task<bool> ConfirmAsync(
        bool confirmed,
        string prompt,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (confirmed)
            return true;

        output.Write(prompt);
        var response = (await input.ReadLineAsync(cancellationToken))?.Trim();
        if (response is "y" or "Y" or "yes" or "Yes" or "YES")
            return true;

        output.WriteLine("Cancelled.");
        return false;
    }

    private static string StatusText(GitSkillPluginApi.PluginStatus status) => status switch
    {
        GitSkillPluginApi.PluginStatus.NotInstalled => "not-installed",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static int WriteHelp(TextWriter output)
    {
        output.WriteLine("Usage: netclaw skill plugin <action>");
        output.WriteLine();
        output.WriteLine("Actions:");
        output.WriteLine("  install <repository> [options]    Validate and install a public GitHub plugin");
        output.WriteLine("  list                              List managed Git skill plugins");
        output.WriteLine("  enable <name>                     Enable a plugin");
        output.WriteLine("  disable <name>                    Disable a plugin");
        output.WriteLine("  remove <name>                     Remove a plugin");
        output.WriteLine();
        output.WriteLine("Install options: --name, --format, --subdirectory, --branch, --tag,");
        output.WriteLine("                 --commit, --timeout-seconds, --yes");
        output.WriteLine("Mutation options: --yes skips the confirmation prompt.");
        return 0;
    }

    private static int WriteUnknownAction(string action, TextWriter output)
    {
        output.WriteLine($"Unknown plugin action '{action}'.");
        WriteHelp(output);
        return 1;
    }
}
