// -----------------------------------------------------------------------
// <copyright file="ConfigDashboardViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Tui.ConfigDashboard;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Sections.Leaves;
using Netclaw.Configuration;
using Netclaw.Providers;
using R3;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.ConfigDashboard;

public sealed class ConfigDashboardViewModelTests
{
    private static SectionEditorRegistry BuildRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ProviderDescriptorRegistry([]));
        services.AddSingleton<IProviderProbe>(new FakeProviderProbe());
        services.AddBootstrapSectionEditors();
        return services.BuildServiceProvider().GetRequiredService<SectionEditorRegistry>();
    }

    [Fact]
    public void RootEntries_MatchSpecOrder()
    {
        // Spec netclaw-config-command/proposal.md lines 24-33 enumerate the
        // root-page domain entries. Order matters for operator muscle memory.
        var vm = new ConfigDashboardViewModel(BuildRegistry());

        var ids = vm.RootEntries.Select(e => e.Id).ToArray();
        Assert.Equal(new[]
        {
            "inference-providers",
            "models",
            "channels",
            "inbound-webhooks",
            "skill-sources",
            "search",
            "browser-automation",
            "telemetry-and-alerting",
            "security-and-access",
        }, ids);
    }

    [Fact]
    public void RoutedEntries_MapToCorrectCommands()
    {
        var vm = new ConfigDashboardViewModel(BuildRegistry());

        var inference = vm.RootEntries.Single(e => e.Id == "inference-providers");
        Assert.Equal(ConfigDashboardEntryKind.Routed, inference.Kind);
        Assert.Equal("netclaw provider", inference.RouteCommand);

        var models = vm.RootEntries.Single(e => e.Id == "models");
        Assert.Equal(ConfigDashboardEntryKind.Routed, models.Kind);
        Assert.Equal("netclaw model", models.RouteCommand);
    }

    [Fact]
    public void DomainEntries_CarryLeafIds_AndOmitMcpPermissions()
    {
        var vm = new ConfigDashboardViewModel(BuildRegistry());

        // Channels area composes the three chat leaves.
        var channels = vm.RootEntries.Single(e => e.Id == "channels");
        Assert.Equal(ConfigDashboardEntryKind.Domain, channels.Kind);
        Assert.Contains("channel-slack", channels.LeafIds);
        Assert.Contains("channel-discord", channels.LeafIds);
        Assert.Contains("channel-mattermost", channels.LeafIds);

        // Security & Access uses the abstraction-aware leaves we registered.
        var sec = vm.RootEntries.Single(e => e.Id == "security-and-access");
        Assert.Equal(ConfigDashboardEntryKind.Domain, sec.Kind);
        Assert.Contains(SectionIds.SecurityPosture, sec.LeafIds);
        Assert.Contains(SectionIds.EnabledFeatures, sec.LeafIds);
        Assert.Contains("audience-profiles", sec.LeafIds);
        Assert.Contains("exposure-mode", sec.LeafIds);

        // MCP permissions are NOT in any leaf id; they route to `netclaw mcp permissions`.
        var allLeafIds = vm.RootEntries.SelectMany(e => e.LeafIds).ToArray();
        Assert.DoesNotContain("mcp-permissions", allLeafIds);
        Assert.DoesNotContain("mcp-servers", allLeafIds);
    }

    [Fact]
    public void TelemetryArea_DefersDeliveryPolicy()
    {
        var vm = new ConfigDashboardViewModel(BuildRegistry());
        var telemetry = vm.RootEntries.Single(e => e.Id == "telemetry-and-alerting");
        Assert.Equal(ConfigDashboardEntryKind.Domain, telemetry.Kind);
        Assert.Contains("telemetry", telemetry.LeafIds);
        Assert.Contains("outbound-webhooks", telemetry.LeafIds);
        // Delivery policy tuning is explicitly deferred per the spec.
        Assert.DoesNotContain("delivery-policy", telemetry.LeafIds);
    }

    [Fact]
    public void SkillSourcesArea_ContainsExternalSkillsAndFeeds()
    {
        var vm = new ConfigDashboardViewModel(BuildRegistry());
        var skills = vm.RootEntries.Single(e => e.Id == "skill-sources");
        Assert.Contains("external-skills", skills.LeafIds);
        Assert.Contains("skill-feeds", skills.LeafIds);
    }

    [Fact]
    public void Registry_IsExposed_ForFutureLeafResolution()
    {
        var registry = BuildRegistry();
        var vm = new ConfigDashboardViewModel(registry);
        Assert.Same(registry, vm.Registry);
    }

    [Fact]
    public void RootAffordances_IncludeQuitAndRunFullDoctor()
    {
        var vm = new ConfigDashboardViewModel(BuildRegistry());

        Assert.Contains(vm.Affordances,
            a => a.Id == "quit" && a.Kind == ConfigDashboardAffordanceKind.Exit);

        var doctor = vm.Affordances.Single(a => a.Id == "doctor");
        Assert.Equal(ConfigDashboardAffordanceKind.RunCommand, doctor.Kind);
        Assert.Equal("netclaw doctor", doctor.RouteCommand);
    }

    [Fact]
    public void ActivateEntry_RoutedHandoff_DispatchesNavigatedToRouteAction()
    {
        // Spec: "Inference Providers routes to `netclaw provider`" — the
        // ViewModel maps spec-text to Termina paths and emits an Action so
        // tests + integration probes can verify routing without a Termina
        // host. Asserts the actual production routing path, not just IA shape.
        using var vm = new ConfigDashboardViewModel(BuildRegistry());
        ConfigDashboardAction? captured = null;
        using var sub = vm.Actions.Subscribe(a => captured = a);

        vm.ActivateEntry(vm.RootEntries.Single(e => e.Id == "inference-providers"));

        Assert.NotNull(captured);
        Assert.Equal(ConfigDashboardActionKind.NavigatedToRoute, captured!.Kind);
        Assert.Equal("/provider", captured.Detail);
    }

    [Fact]
    public void ActivateEntry_DomainEntry_OpensSubMenu()
    {
        using var vm = new ConfigDashboardViewModel(BuildRegistry());
        ConfigDashboardAction? captured = null;
        using var sub = vm.Actions.Subscribe(a => captured = a);

        var channels = vm.RootEntries.Single(e => e.Id == "channels");
        vm.ActivateEntry(channels);

        Assert.NotNull(captured);
        Assert.Equal(ConfigDashboardActionKind.OpenedDomain, captured!.Kind);
        Assert.Same(channels, vm.ActiveDomain.Value);
    }

    [Fact]
    public void GoBackToRoot_FromSubMenu_ClearsActiveDomain()
    {
        using var vm = new ConfigDashboardViewModel(BuildRegistry());
        vm.ActivateEntry(vm.RootEntries.Single(e => e.Id == "channels"));
        Assert.NotNull(vm.ActiveDomain.Value);

        vm.GoBackToRoot();

        Assert.Null(vm.ActiveDomain.Value);
    }

    [Fact]
    public void MapRouteCommandToTerminaPath_KnownCommands()
    {
        Assert.Equal("/provider",
            ConfigDashboardViewModel.MapRouteCommandToTerminaPath("netclaw provider"));
        Assert.Equal("/model",
            ConfigDashboardViewModel.MapRouteCommandToTerminaPath("netclaw model"));
        Assert.Equal("/mcp-tools",
            ConfigDashboardViewModel.MapRouteCommandToTerminaPath("netclaw mcp permissions"));
    }

    [Fact]
    public void MapRouteCommandToTerminaPath_UnknownCommand_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ConfigDashboardViewModel.MapRouteCommandToTerminaPath("netclaw nonexistent"));
    }
}
