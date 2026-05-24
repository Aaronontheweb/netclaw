// -----------------------------------------------------------------------
// <copyright file="AudienceProfilesShellModePreservationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Sections.Leaves;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Sections.Leaves;

/// <summary>
/// Audit-finding regression (H4): the curated Audience Profiles editor
/// must NOT silently clobber Tools.ShellMode on save. Shell mode is on
/// the spec's forbidden-surfaces list — Audience Profiles SHALL NOT
/// touch it.
/// </summary>
public sealed class AudienceProfilesShellModePreservationTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public AudienceProfilesShellModePreservationTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ContributeConfig_PreservesShellMode_FromExistingConfig()
    {
        // Existing install has ShellMode = HostAllowed (operator allowed
        // shell tools previously). The curated Audience Profiles editor
        // SHALL NOT downgrade that to Off on save.
        var editor = new AudienceProfilesSectionEditor();
        using var wizardContext = new WizardContext
        {
            Paths = _paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { },
            ExistingConfig = new Dictionary<string, object>
            {
                ["Tools"] = new Dictionary<string, object>
                {
                    ["ShellMode"] = "HostAllowed",
                    ["AudienceProfiles"] = new Dictionary<string, object>(),
                },
            },
        };

        var step = (AudienceProfilesStepViewModel)editor.CreateEditor(wizardContext);
        var builder = new WizardConfigBuilder(_paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Tools);
        Assert.Equal(ShellExecutionMode.HostAllowed, builder.Tools!.ShellMode);
    }

    [Fact]
    public void ContributeConfig_NoExistingConfig_DefaultsToOff()
    {
        // Fresh install — no Tools section on disk yet. Defaulting to Off
        // is acceptable because the operator hasn't opted in.
        var editor = new AudienceProfilesSectionEditor();
        using var wizardContext = new WizardContext
        {
            Paths = _paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { },
            ExistingConfig = null,
        };

        var step = (AudienceProfilesStepViewModel)editor.CreateEditor(wizardContext);
        var builder = new WizardConfigBuilder(_paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Tools);
        Assert.Equal(ShellExecutionMode.Off, builder.Tools!.ShellMode);
    }
}
