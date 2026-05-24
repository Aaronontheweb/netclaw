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
}
