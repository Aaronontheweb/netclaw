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
}
