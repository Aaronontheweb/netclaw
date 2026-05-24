// -----------------------------------------------------------------------
// <copyright file="WizardConfigBuilderMergeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Channels.Slack;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

/// <summary>
/// Regression coverage for the merge-on-save semantics introduced in
/// section-editor-abstraction § 5. Validates that re-running init with a
/// feature unchecked actually disables the feature (no stale residue) and
/// that the daemon's ExposureMode transition to Local clears the previous
/// listener-binding fields.
/// </summary>
public sealed class WizardConfigBuilderMergeTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public WizardConfigBuilderMergeTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void WriteConfig_PreservesUnrelatedTopLevelSections()
    {
        // Pre-existing config has McpServers added externally
        // (e.g., `netclaw mcp install <foo>`).
        File.WriteAllText(_paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "McpServers": { "playwright": { "Enabled": true, "Command": "npx" } }
            }
            """);

        var builder = new WizardConfigBuilder(_paths);
        builder.Workspaces = new WorkspacesConfigSection { Directory = "/tmp/workspaces" };
        builder.WriteConfigFile();

        var obj = JsonNode.Parse(File.ReadAllText(_paths.NetclawConfigPath))!.AsObject();
        Assert.True(obj["McpServers"]!.AsObject().ContainsKey("playwright"));
        Assert.Equal("/tmp/workspaces", obj["Workspaces"]!["Directory"]!.GetValue<string>());
    }

    [Fact]
    public void WriteConfig_DisableSlack_RemovesSection()
    {
        // Pre-existing config has Slack enabled.
        File.WriteAllText(_paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Slack": { "Enabled": true, "DefaultChannelId": "C123" }
            }
            """);

        // Re-run with Slack step that owner unchecked.
        using var slack = new SlackStepViewModel(new NoopSlackProbe());
        slack.SlackEnabled = false;
        var builder = new WizardConfigBuilder(_paths);
        slack.ContributeConfig(builder);
        builder.WriteConfigFile();

        var obj = JsonNode.Parse(File.ReadAllText(_paths.NetclawConfigPath))!.AsObject();
        Assert.False(obj.ContainsKey("Slack"),
            "Disabling Slack via the wizard SHALL remove the section, not preserve stale state.");
    }

    private sealed class NoopSlackProbe : ISlackProbe
    {
        public Task<SlackProbeResult> ProbeAsync(string botToken, CancellationToken ct = default)
            => Task.FromResult(new SlackProbeResult(false, null, null, null));

        public Task<SlackChannelResolutionResult> ResolveChannelNamesAsync(
            string botToken, IReadOnlyList<string> channelNames, CancellationToken ct = default)
            => Task.FromResult(new SlackChannelResolutionResult(true, null, [], []));
    }

    [Fact]
    public void WriteConfig_DaemonLocal_RemovesStalePublicBinding()
    {
        // Pre-existing config has ReverseProxy mode with a public host binding.
        File.WriteAllText(_paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "ReverseProxy",
                "Host": "0.0.0.0",
                "TrustedProxies": ["10.0.0.0/8"]
              }
            }
            """);

        // Re-run with ExposureMode step transitioned to Local.
        var exposure = new ExposureModeStepViewModel();
        exposure.SelectedMode = ExposureMode.Local;
        var builder = new WizardConfigBuilder(_paths);
        exposure.ContributeConfig(builder);
        builder.WriteConfigFile();

        var obj = JsonNode.Parse(File.ReadAllText(_paths.NetclawConfigPath))!.AsObject();
        Assert.False(obj.ContainsKey("Daemon"),
            "Daemon → Local SHALL clear the prior section so the daemon does not keep listening on the public interface.");
    }

    [Fact]
    public void WriteConfig_AtomicWrite_RecoversFromCorruptExisting()
    {
        // Corrupt JSON on disk — save SHALL succeed by backing up and starting fresh.
        File.WriteAllText(_paths.NetclawConfigPath, "{ broken");

        var builder = new WizardConfigBuilder(_paths);
        builder.Workspaces = new WorkspacesConfigSection { Directory = "/tmp/workspaces" };

        builder.WriteConfigFile(); // SHALL NOT throw

        var newFile = JsonSerializer.Deserialize<JsonNode>(
            File.ReadAllText(_paths.NetclawConfigPath))!.AsObject();
        Assert.Equal("/tmp/workspaces", newFile["Workspaces"]!["Directory"]!.GetValue<string>());

        // The corrupt file SHALL be preserved with a .corrupt.* suffix.
        var dir = Path.GetDirectoryName(_paths.NetclawConfigPath)!;
        var backups = Directory.GetFiles(dir,
            Path.GetFileName(_paths.NetclawConfigPath) + ".corrupt.*");
        Assert.NotEmpty(backups);
    }
}
