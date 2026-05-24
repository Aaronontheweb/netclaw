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

    [Fact]
    public void CreateEditor_FromExistingConfig_PrefillsNonSecretFieldsButNotCredential()
    {
        // netclaw-onboarding spec scenario: "Provider re-entry keeps credential field masked"
        var editor = CreateEditor();
        using var wizardContext = BuildWizardContext(existingConfig: new Dictionary<string, object>
        {
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["Endpoint"] = "https://api.example.com",
                    ["AuthMethod"] = "ApiKey",
                },
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = "openai",
                    ["ModelId"] = "gpt-4o",
                },
            },
        });

        var step = (Netclaw.Cli.Tui.Wizard.Steps.ProviderStepViewModel)
            editor.CreateEditor(wizardContext);

        Assert.Equal("openai", step.SelectedProviderType);
        Assert.Equal("https://api.example.com", step.EndpointInput);
        Assert.Equal(Netclaw.Configuration.AuthMethod.ApiKey, step.SelectedAuthMethod);
        Assert.Equal("gpt-4o", step.SelectedModelId);

        // Credential SHALL NOT be prefilled. The view uses
        // ConfigFileHelper.SecretPresent for "configured — leave blank" hinting.
        Assert.Null(step.ApiKeyInput);
    }
}
