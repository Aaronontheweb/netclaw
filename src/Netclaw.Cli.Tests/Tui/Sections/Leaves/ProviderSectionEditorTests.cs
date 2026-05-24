// -----------------------------------------------------------------------
// <copyright file="ProviderSectionEditorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Sections.Leaves;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Sections.Leaves;

public sealed class ProviderSectionEditorTests : SectionEditorTestBase<ProviderSectionEditor>
{
    protected override ProviderSectionEditor CreateEditor() =>
        new(new ProviderDescriptorRegistry([]), new FakeProviderProbe());

    [Fact]
    public void Identity_ShowsInMenu_False_RoutedToProviderCommand()
    {
        var editor = CreateEditor();
        Assert.Equal(SectionIds.Provider, editor.SectionId);
        Assert.False(editor.ShowInMenu,
            "Provider stays out of the config dashboard menu — handoff goes to `netclaw provider`.");
    }

    [Fact]
    public void GetStatus_WithProvidersSection_ReportsConfigured()
    {
        var editor = CreateEditor();
        var context = BuildContext(new Dictionary<string, object>
        {
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai"] = new Dictionary<string, object> { ["Type"] = "openai" },
            },
        });

        Assert.Equal(SectionStatus.Configured, editor.GetStatus(context));
        Assert.Contains("openai", editor.Summary(context));
    }

    [Fact]
    public void RelevantDoctorChecks_DeclaresContextWindowAndSecrets()
    {
        var editor = CreateEditor();
        Assert.Contains(typeof(Netclaw.Cli.Doctor.ContextWindowDoctorCheck), editor.RelevantDoctorChecks);
        Assert.Contains(typeof(Netclaw.Cli.Doctor.SecretsJsonDoctorCheck), editor.RelevantDoctorChecks);
    }
}
