// -----------------------------------------------------------------------
// <copyright file="EnabledFeaturesSectionEditorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Sections.Leaves;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Sections.Leaves;

public sealed class EnabledFeaturesSectionEditorTests
    : SectionEditorTestBase<EnabledFeaturesSectionEditor>
{
    protected override EnabledFeaturesSectionEditor CreateEditor() => new();

    [Fact]
    public void Identity_ShowsInMenu_UnderSecurityAndAccess()
    {
        var editor = CreateEditor();
        Assert.Equal(SectionIds.EnabledFeatures, editor.SectionId);
        Assert.True(editor.ShowInMenu);
        Assert.Equal("Security & Access", editor.Category);
    }

    [Fact]
    public void GetStatus_WithMemoryEnabled_ReportsConfigured()
    {
        var editor = CreateEditor();
        var context = BuildContext(new Dictionary<string, object>
        {
            ["Memory"] = new Dictionary<string, object> { ["Enabled"] = true },
        });
        Assert.Equal(SectionStatus.Configured, editor.GetStatus(context));
        Assert.Contains("Memory", editor.Summary(context));
    }

    [Fact]
    public void Summary_WithMixedToggles_OnlyShowsEnabled()
    {
        var editor = CreateEditor();
        var context = BuildContext(new Dictionary<string, object>
        {
            ["Memory"] = new Dictionary<string, object> { ["Enabled"] = true },
            ["Search"] = new Dictionary<string, object> { ["Enabled"] = false },
            ["SkillSync"] = new Dictionary<string, object> { ["Enabled"] = true },
        });

        var summary = editor.Summary(context);
        Assert.Contains("Memory", summary);
        Assert.Contains("SkillSync", summary);
        Assert.DoesNotContain("Search", summary);
    }

    [Fact]
    public void NoDoctorChecks_IsExplicitlyJustified()
    {
        var attr = (NoDoctorChecksAttribute?)Attribute.GetCustomAttribute(
            typeof(EnabledFeaturesSectionEditor), typeof(NoDoctorChecksAttribute));
        Assert.NotNull(attr);
        Assert.Contains("doctor", attr.Justification, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateEditor_FromExistingConfig_PrefillsToggleRow()
    {
        // Verify-gap H1: this leaf previously returned a fresh step viewmodel,
        // visually resetting every feature on re-entry. The fix mirrors the
        // SecurityPosture / Identity / Provider prefill contract.
        var editor = CreateEditor();
        using var wizardContext = BuildWizardContext(existingConfig: new Dictionary<string, object>
        {
            ["Memory"] = new Dictionary<string, object> { ["Enabled"] = true },
            ["Search"] = new Dictionary<string, object> { ["Enabled"] = false },
            ["SkillSync"] = new Dictionary<string, object> { ["Enabled"] = true },
            ["Scheduling"] = new Dictionary<string, object> { ["Enabled"] = false },
            ["SubAgents"] = new Dictionary<string, object> { ["Enabled"] = true },
            ["Webhooks"] = new Dictionary<string, object> { ["Enabled"] = false },
        });

        var step = (Netclaw.Cli.Tui.Wizard.Steps.FeatureSelectionStepViewModel)
            editor.CreateEditor(wizardContext);

        // Order matches FeatureKeys in EnabledFeaturesSectionEditor:
        // Memory(0), Search(1), SkillSync(2), Scheduling(3), SubAgents(4), Webhooks(5).
        Assert.True(step.IsFeatureEnabled(0));   // Memory
        Assert.False(step.IsFeatureEnabled(1));  // Search
        Assert.True(step.IsFeatureEnabled(2));   // SkillSync
        Assert.False(step.IsFeatureEnabled(3));  // Scheduling
        Assert.True(step.IsFeatureEnabled(4));   // SubAgents
        Assert.False(step.IsFeatureEnabled(5));  // Webhooks
    }
}
