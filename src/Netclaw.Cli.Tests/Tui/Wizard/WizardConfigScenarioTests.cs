// -----------------------------------------------------------------------
// <copyright file="WizardConfigScenarioTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

/// <summary>
/// End-to-end scenario tests that simulate complete wizard flows and verify the
/// entire assembled config dictionary matches the user's choices. Each scenario
/// exercises the real step ViewModels through the config assembly pipeline.
/// </summary>
public sealed class WizardConfigScenarioTests : WizardStepTestBase
{
    [Fact]
    public void PersonalPosture_MinimalSetup_DoesNotDisableFeatures()
    {
        var steps = BuildCoreSteps();
        EnterAndConfigurePosture(steps, DeploymentPosture.Personal);
        ConfigureSearch(steps, SearchBackend.Brave);
        ConfigureExposure(steps, ExposureMode.Local, webhooks: false);
        ConfigureIdentity(steps, "Netclaw", "America/Chicago");

        var config = AssembleConfig(steps);

        AssertPosture(config, "Personal");
        AssertShellMode(config, "HostAllowed");
        AssertSearchBackend(config, "brave");
        Assert.False(config.ContainsKey("Daemon"));

        // The bug: Personal posture must not inject Enabled:false for any feature
        AssertNoDisabledFeatureFlags(config);
    }

    [Fact]
    public void TeamPosture_AllFeaturesEnabled()
    {
        var steps = BuildCoreSteps();
        EnterAndConfigurePosture(steps, DeploymentPosture.Team);
        EnterFeatureSelection(steps, DeploymentPosture.Team);
        ConfigureSearch(steps, SearchBackend.DuckDuckGo);
        ConfigureExposure(steps, ExposureMode.TailscaleServe, webhooks: true);
        ConfigureIdentity(steps, "TeamBot", "UTC");

        var config = AssembleConfig(steps);

        AssertPosture(config, "Team");
        AssertShellMode(config, "Off");
        AssertSectionEnabled(config, "Memory", true);
        AssertSectionEnabled(config, "Search", true);
        AssertSectionEnabled(config, "SkillSync", true);
        AssertSectionEnabled(config, "Scheduling", true);
        AssertSectionEnabled(config, "SubAgents", true);
        AssertSectionEnabled(config, "Webhooks", true);

        var daemon = GetSection(config, "Daemon");
        Assert.Equal("tailscale-serve", daemon["ExposureMode"]);
    }

    [Fact]
    public void PublicPosture_SelectiveFeatures()
    {
        var steps = BuildCoreSteps();
        EnterAndConfigurePosture(steps, DeploymentPosture.Public);

        // Public defaults all OFF — toggle on Memory and Search only
        var featureStep = GetStep<FeatureSelectionStepViewModel>(steps);
        featureStep.OnEnter(Context, NavigationDirection.Forward);
        featureStep.ToggleFeature(0); // Memory
        featureStep.ToggleFeature(1); // Search
        featureStep.OnLeave();

        ConfigureSearch(steps, SearchBackend.SearXng, searXngEndpoint: "https://search.example.com");
        ConfigureExposure(steps, ExposureMode.TailscaleFunnel, webhooks: false);
        ConfigureIdentity(steps, "PublicBot", "Europe/London");

        var config = AssembleConfig(steps);

        AssertPosture(config, "Public");
        AssertSectionEnabled(config, "Memory", true);
        AssertSectionEnabled(config, "Search", true);
        AssertSectionEnabled(config, "SkillSync", false);
        AssertSectionEnabled(config, "Scheduling", false);
        AssertSectionEnabled(config, "SubAgents", false);
        AssertSectionEnabled(config, "Webhooks", false);

        var search = GetSection(config, "Search");
        Assert.Equal("searxng", search["Backend"]);
        Assert.Equal("https://search.example.com", search["SearXngEndpoint"]);

        var daemon = GetSection(config, "Daemon");
        Assert.Equal("tailscale-funnel", daemon["ExposureMode"]);
    }

    [Fact]
    public void TeamPosture_SelectivelyDisabledFeatures()
    {
        var steps = BuildCoreSteps();
        EnterAndConfigurePosture(steps, DeploymentPosture.Team);

        // Team defaults all ON — toggle off Memory and Scheduling
        var featureStep = GetStep<FeatureSelectionStepViewModel>(steps);
        featureStep.OnEnter(Context, NavigationDirection.Forward);
        featureStep.ToggleFeature(0); // Memory OFF
        featureStep.ToggleFeature(3); // Scheduling OFF
        featureStep.OnLeave();

        ConfigureSearch(steps, SearchBackend.Brave);
        ConfigureExposure(steps, ExposureMode.Local, webhooks: false);
        ConfigureIdentity(steps, "Netclaw", "America/New_York");

        var config = AssembleConfig(steps);

        AssertSectionEnabled(config, "Memory", false);
        AssertSectionEnabled(config, "Search", true);
        AssertSectionEnabled(config, "SkillSync", true);
        AssertSectionEnabled(config, "Scheduling", false);
        AssertSectionEnabled(config, "SubAgents", true);
        AssertSectionEnabled(config, "Webhooks", true);
    }

    [Fact]
    public void PersonalPosture_WithIdentityAndWorkspaces_ConfigMatchesChoices()
    {
        var steps = BuildCoreSteps();
        EnterAndConfigurePosture(steps, DeploymentPosture.Personal);
        ConfigureSearch(steps, SearchBackend.DuckDuckGo);
        ConfigureExposure(steps, ExposureMode.Local, webhooks: false);

        var identityStep = GetStep<IdentityStepViewModel>(steps);
        identityStep.AgentName = "Jarvis";
        identityStep.UserName = "Aaron";
        identityStep.UserTimezone = "America/Chicago";
        identityStep.WorkspacesDirectory = "~/projects";

        var config = AssembleConfig(steps);

        // Identity is written to separate files, not the config dict.
        // Workspaces IS in the config dict.
        var workspaces = GetSection(config, "Workspaces");
        Assert.Equal("~/projects", workspaces["Directory"]);

        AssertNoDisabledFeatureFlags(config);
    }

    [Fact]
    public void PersonalPosture_ExposureModeLocal_NoDaemonSection()
    {
        var steps = BuildCoreSteps();
        EnterAndConfigurePosture(steps, DeploymentPosture.Personal);
        ConfigureSearch(steps, SearchBackend.DuckDuckGo);
        ConfigureExposure(steps, ExposureMode.Local, webhooks: false);
        ConfigureIdentity(steps, "Netclaw", "UTC");

        var config = AssembleConfig(steps);

        Assert.False(config.ContainsKey("Daemon"));
        // Webhooks section should not appear when webhooks are off and
        // there's no FeatureSelection step to explicitly write it
        Assert.False(config.ContainsKey("Webhooks") && GetSection(config, "Webhooks").ContainsKey("Enabled")
            && (bool)GetSection(config, "Webhooks")["Enabled"]);
    }

    [Fact]
    public void TeamPosture_ExposureTailscaleFunnel_WebhooksOn()
    {
        var steps = BuildCoreSteps();
        EnterAndConfigurePosture(steps, DeploymentPosture.Team);
        EnterFeatureSelection(steps, DeploymentPosture.Team);
        ConfigureSearch(steps, SearchBackend.Brave);
        ConfigureExposure(steps, ExposureMode.TailscaleFunnel, webhooks: true);
        ConfigureIdentity(steps, "Netclaw", "UTC");

        var config = AssembleConfig(steps);

        var daemon = GetSection(config, "Daemon");
        Assert.Equal("tailscale-funnel", daemon["ExposureMode"]);

        // Webhooks: both the feature gate and the exposure step contribute
        AssertSectionEnabled(config, "Webhooks", true);
    }

    // ── Helpers ──

    private List<IWizardStepViewModel> BuildCoreSteps()
    {
        return
        [
            new SecurityPostureStepViewModel(),
            new FeatureSelectionStepViewModel(),
            new SearchStepViewModel(),
            new IdentityStepViewModel(),
            new ExposureModeStepViewModel()
        ];
    }

    private void EnterAndConfigurePosture(List<IWizardStepViewModel> steps, DeploymentPosture posture)
    {
        var step = GetStep<SecurityPostureStepViewModel>(steps);
        step.OnEnter(Context, NavigationDirection.Forward);
        step.SelectedPosture = posture;
        step.OnLeave();
        Context.SelectedPosture = posture;
    }

    private void EnterFeatureSelection(List<IWizardStepViewModel> steps, DeploymentPosture posture)
    {
        var step = GetStep<FeatureSelectionStepViewModel>(steps);
        if (!step.IsApplicable(Context))
            return;
        step.OnEnter(Context, NavigationDirection.Forward);
        step.OnLeave();
    }

    private static void ConfigureSearch(List<IWizardStepViewModel> steps, SearchBackend backend,
        string? searXngEndpoint = null)
    {
        var step = GetStep<SearchStepViewModel>(steps);
        step.SelectedBackend = backend;
        if (searXngEndpoint is not null)
            step.SearXngEndpoint = searXngEndpoint;
    }

    private static void ConfigureExposure(List<IWizardStepViewModel> steps, ExposureMode mode, bool webhooks)
    {
        var step = GetStep<ExposureModeStepViewModel>(steps);
        step.SelectedMode = mode;
        step.WebhooksEnabled = webhooks;
    }

    private static void ConfigureIdentity(List<IWizardStepViewModel> steps, string name, string timezone)
    {
        var step = GetStep<IdentityStepViewModel>(steps);
        step.AgentName = name;
        step.UserTimezone = timezone;
    }

    private Dictionary<string, object> AssembleConfig(List<IWizardStepViewModel> steps)
    {
        var builder = new WizardConfigBuilder(Context.Paths);
        foreach (var step in steps.Where(s => s.IsApplicable(Context)))
            step.ContributeConfig(builder);
        return builder.BuildConfigDictionary();
    }

    private static T GetStep<T>(List<IWizardStepViewModel> steps) where T : IWizardStepViewModel
        => steps.OfType<T>().Single();

    private static Dictionary<string, object> GetSection(Dictionary<string, object> config, string key)
        => (Dictionary<string, object>)config[key];

    private static void AssertPosture(Dictionary<string, object> config, string expected)
    {
        var security = GetSection(config, "Security");
        Assert.Equal(expected, security["DeploymentPosture"]);
    }

    private static void AssertShellMode(Dictionary<string, object> config, string expected)
    {
        var security = GetSection(config, "Security");
        Assert.Equal(expected, security["ShellExecutionMode"]);
    }

    private static void AssertSearchBackend(Dictionary<string, object> config, string expected)
    {
        var search = GetSection(config, "Search");
        Assert.Equal(expected, search["Backend"]);
    }

    private static void AssertSectionEnabled(Dictionary<string, object> config, string sectionKey, bool expected)
    {
        Assert.True(config.ContainsKey(sectionKey), $"Config should contain '{sectionKey}' section");
        var section = GetSection(config, sectionKey);
        Assert.Equal(expected, section["Enabled"]);
    }

    private static void AssertNoDisabledFeatureFlags(Dictionary<string, object> config)
    {
        string[] featureSections = ["Memory", "Scheduling", "SubAgents"];
        foreach (var section in featureSections)
        {
            if (config.TryGetValue(section, out var obj) && obj is Dictionary<string, object> dict)
            {
                Assert.False(dict.TryGetValue("Enabled", out var enabled) && enabled is false,
                    $"Section '{section}' should not have Enabled=false for Personal posture");
            }
        }
    }
}
