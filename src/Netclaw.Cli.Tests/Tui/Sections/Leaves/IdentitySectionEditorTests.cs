// -----------------------------------------------------------------------
// <copyright file="IdentitySectionEditorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Sections.Leaves;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Sections.Leaves;

public sealed class IdentitySectionEditorTests : SectionEditorTestBase<IdentitySectionEditor>
{
    protected override IdentitySectionEditor CreateEditor() => new();

    [Fact]
    public void Identity_IsSyntheticInitOwned_NotInMenu()
    {
        var editor = CreateEditor();
        Assert.Equal(SectionIds.Identity, editor.SectionId);
        Assert.False(editor.ShowInMenu,
            "Identity is synthetic + init-owned per Decision D4; it MUST NOT appear in the config menu.");
        Assert.True(SectionEditorExemptions.IsSyntheticInitOwned(editor.SectionId),
            "Identity SHALL be listed in SectionEditorExemptions.SyntheticInitOwnedIds.");
    }

    [Fact]
    public void GetStatus_WithIdentityAgentName_ReportsConfigured()
    {
        var editor = CreateEditor();
        var context = BuildContext(new Dictionary<string, object>
        {
            ["Identity"] = new Dictionary<string, object>
            {
                ["AgentName"] = "Netclaw",
            },
        });

        Assert.Equal(SectionStatus.Configured, editor.GetStatus(context));
        Assert.Contains("Netclaw", editor.Summary(context));
    }

    [Fact]
    public void GetStatus_EmptyAgentName_ReportsNotConfigured()
    {
        var editor = CreateEditor();
        var context = BuildContext(new Dictionary<string, object>
        {
            ["Identity"] = new Dictionary<string, object>
            {
                ["AgentName"] = "  ",
            },
        });

        Assert.Equal(SectionStatus.NotConfigured, editor.GetStatus(context));
    }

    [Fact]
    public void DoctorChecks_EmptyByDesign_ButJustificationDeclared()
    {
        var editor = CreateEditor();
        Assert.Empty(editor.RelevantDoctorChecks);

        // The contract test in the base class also enforces this, but verify
        // explicitly so the audit reviewer sees the justification flow here.
        var attr = (NoDoctorChecksAttribute?)Attribute.GetCustomAttribute(
            typeof(IdentitySectionEditor), typeof(NoDoctorChecksAttribute));
        Assert.NotNull(attr);
        Assert.False(string.IsNullOrWhiteSpace(attr.Justification));
    }

    [Fact]
    public void CreateEditor_FromExistingConfig_PrefillsNonSecretFields()
    {
        // netclaw-onboarding spec scenario: "Identity re-entry prefills init-owned fields"
        var editor = CreateEditor();
        using var wizardContext = BuildWizardContext(existingConfig: new Dictionary<string, object>
        {
            ["Identity"] = new Dictionary<string, object>
            {
                ["AgentName"] = "Aria",
                ["CommunicationStyle"] = "Detailed & formal",
                ["UserName"] = "Aaron",
                ["UserTimezone"] = "America/New_York",
            },
            ["Workspaces"] = new Dictionary<string, object>
            {
                ["Directory"] = "/srv/workspaces",
            },
        });

        var step = (Netclaw.Cli.Tui.Wizard.Steps.IdentityStepViewModel)
            editor.CreateEditor(wizardContext);

        Assert.Equal("Aria", step.AgentName);
        Assert.Equal("Detailed & formal", step.CommunicationStyle);
        Assert.Equal("Aaron", step.UserName);
        Assert.Equal("America/New_York", step.UserTimezone);
        Assert.Equal("/srv/workspaces", step.WorkspacesDirectory);
    }

    [Fact]
    public void CreateEditor_FreshInstall_UsesDefaults()
    {
        var editor = CreateEditor();
        using var wizardContext = BuildWizardContext();
        var step = (Netclaw.Cli.Tui.Wizard.Steps.IdentityStepViewModel)
            editor.CreateEditor(wizardContext);

        // Default AgentName is "Netclaw" per the step viewmodel.
        Assert.Equal("Netclaw", step.AgentName);
    }
}
