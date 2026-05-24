// -----------------------------------------------------------------------
// <copyright file="SecurityPostureSectionEditorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Sections.Leaves;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Sections.Leaves;

public sealed class SecurityPostureSectionEditorTests
    : SectionEditorTestBase<SecurityPostureSectionEditor>
{
    protected override SecurityPostureSectionEditor CreateEditor() => new();

    [Fact]
    public void Identity_ShowsInMenu_UnderSecurityAndAccess()
    {
        var editor = CreateEditor();
        Assert.Equal(SectionIds.SecurityPosture, editor.SectionId);
        Assert.True(editor.ShowInMenu);
        Assert.Equal("Security & Access", editor.Category);
    }

    [Fact]
    public void GetStatus_WithPosture_ReportsConfigured()
    {
        var editor = CreateEditor();
        var context = BuildContext(new Dictionary<string, object>
        {
            ["Security"] = new Dictionary<string, object>
            {
                ["DeploymentPosture"] = "Public",
            },
        });
        Assert.Equal(SectionStatus.Configured, editor.GetStatus(context));
        Assert.Contains("Public", editor.Summary(context));
    }

    [Fact]
    public void RelevantDoctorChecks_DeclaresSecurityAndToolAudienceChecks()
    {
        var editor = CreateEditor();
        Assert.Contains(typeof(Netclaw.Cli.Doctor.SecurityPolicyDoctorCheck), editor.RelevantDoctorChecks);
        Assert.Contains(typeof(Netclaw.Cli.Doctor.ToolAudienceProfilesDoctorCheck), editor.RelevantDoctorChecks);
    }

    [Fact]
    public void CreateEditor_FromExistingConfig_PreselectsCurrentPosture()
    {
        // section-editor-abstraction spec scenario: "Existing non-secret values prefill"
        // — an init-owned flow that enters the Security Posture editor with
        // an existing posture configured SHALL preselect that posture.
        var editor = CreateEditor();
        using var wizardContext = BuildWizardContext(existingConfig: new Dictionary<string, object>
        {
            ["Security"] = new Dictionary<string, object>
            {
                ["DeploymentPosture"] = "Public",
            },
        });

        var step = (Netclaw.Cli.Tui.Wizard.Steps.SecurityPostureStepViewModel)
            editor.CreateEditor(wizardContext);

        Assert.Equal(Netclaw.Configuration.DeploymentPosture.Public, step.SelectedPosture);
    }

    [Fact]
    public void CreateEditor_FreshInstall_LeavesPostureUnselected()
    {
        var editor = CreateEditor();
        using var wizardContext = BuildWizardContext();
        var step = (Netclaw.Cli.Tui.Wizard.Steps.SecurityPostureStepViewModel)
            editor.CreateEditor(wizardContext);
        Assert.Null(step.SelectedPosture);
    }
}
