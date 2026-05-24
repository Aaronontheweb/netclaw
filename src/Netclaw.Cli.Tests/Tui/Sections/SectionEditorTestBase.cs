// -----------------------------------------------------------------------
// <copyright file="SectionEditorTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Sections;

/// <summary>
/// Base harness for <see cref="ISectionEditor"/> round-trip tests. Asserts
/// the contract from <c>section-editor-abstraction</c> §spec:
/// <list type="bullet">
///   <item>The editor's <see cref="ISectionEditor.SectionId"/> matches a
///     non-empty stable identifier.</item>
///   <item>Either <see cref="ISectionEditor.RelevantDoctorChecks"/> is
///     non-empty OR the editor type carries <see cref="NoDoctorChecksAttribute"/>
///     with a non-empty justification.</item>
///   <item>The editor produces a valid <see cref="IWizardStepViewModel"/>
///     that disposes cleanly.</item>
///   <item>Status / Summary do not throw for a fresh-install context.</item>
///   <item>Per-editor round-trip scenarios (overridden by subclasses) cover
///     status, summary, and config-state preservation.</item>
/// </list>
/// </summary>
public abstract class SectionEditorTestBase<TEditor> : IDisposable
    where TEditor : ISectionEditor
{
    private readonly DisposableTempDir _dir;
    protected readonly NetclawPaths Paths;

    protected SectionEditorTestBase()
    {
        _dir = new DisposableTempDir();
        Paths = new NetclawPaths(_dir.Path);
        Paths.EnsureDirectoriesExist();
    }

    public virtual void Dispose() => _dir.Dispose();

    /// <summary>Subclass hook: construct the editor under test.</summary>
    protected abstract TEditor CreateEditor();

    [Fact]
    public void Contract_SectionId_IsNonEmpty()
    {
        var editor = CreateEditor();
        Assert.False(string.IsNullOrWhiteSpace(editor.SectionId),
            "ISectionEditor.SectionId SHALL be a non-empty stable identifier.");
    }

    [Fact]
    public void Contract_DisplayName_IsNonEmpty()
    {
        var editor = CreateEditor();
        Assert.False(string.IsNullOrWhiteSpace(editor.DisplayName),
            "ISectionEditor.DisplayName SHALL be a non-empty user-facing string.");
    }

    [Fact]
    public void Contract_DoctorCoverage_DeclaredOrJustified()
    {
        var editor = CreateEditor();
        if (editor.RelevantDoctorChecks.Count > 0)
            return;

        var attr = editor.GetType()
            .GetCustomAttributes(typeof(NoDoctorChecksAttribute), inherit: false)
            .Cast<NoDoctorChecksAttribute>()
            .FirstOrDefault();

        Assert.NotNull(attr);
        Assert.False(string.IsNullOrWhiteSpace(attr.Justification),
            "[NoDoctorChecks] requires a non-empty justification.");
    }

    [Fact]
    public void Contract_FreshInstall_StatusIsNotConfigured()
    {
        var editor = CreateEditor();
        var context = BuildContext(config: new Dictionary<string, object>());
        var status = editor.GetStatus(context);
        Assert.Equal(SectionStatus.NotConfigured, status);
    }

    [Fact]
    public void Contract_FreshInstall_SummaryDoesNotThrow()
    {
        var editor = CreateEditor();
        var context = BuildContext(config: new Dictionary<string, object>());
        var summary = editor.Summary(context);
        Assert.NotNull(summary);
    }

    [Fact]
    public void Contract_CreateEditor_ReturnsDisposableStep()
    {
        var editor = CreateEditor();
        using var wizardContext = BuildWizardContext();
        var step = editor.CreateEditor(wizardContext);
        Assert.NotNull(step);
        Assert.False(string.IsNullOrWhiteSpace(step.StepId));
        step.Dispose();
    }

    [Fact]
    public void Contract_DoctorChecks_AllImplementIDoctorCheck()
    {
        var editor = CreateEditor();
        foreach (var t in editor.RelevantDoctorChecks)
        {
            Assert.True(
                typeof(Netclaw.Cli.Doctor.IDoctorCheck).IsAssignableFrom(t),
                $"{editor.GetType().Name} declared {t.FullName} as a doctor check but it does not implement IDoctorCheck.");
        }
    }

    /// <summary>
    /// Build a <see cref="SectionEditorContext"/> for status/summary
    /// scenarios. <paramref name="config"/> is the on-disk netclaw.json
    /// shape (top-level dict); <paramref name="secretPresent"/> defaults
    /// to "no secrets stored anywhere".
    /// </summary>
    protected SectionEditorContext BuildContext(
        IReadOnlyDictionary<string, object> config,
        Func<string, bool>? secretPresent = null)
    {
        return new SectionEditorContext(
            paths: Paths,
            config: config,
            secretPresent: secretPresent ?? (_ => false));
    }

    /// <summary>Build a no-op <see cref="WizardContext"/> for editor invocation.</summary>
    protected WizardContext BuildWizardContext(
        IReadOnlyDictionary<string, object>? existingConfig = null)
    {
        return new WizardContext
        {
            Paths = Paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { },
            ExistingConfig = existingConfig,
        };
    }

    /// <summary>
    /// Run a semantic round-trip: take an initial config, save through the
    /// builder with the supplied mutation, reload, and return the merged
    /// dict so subclasses can assert preservation.
    /// </summary>
    protected static Dictionary<string, object> RoundTrip(
        NetclawPaths paths,
        IReadOnlyDictionary<string, object> initialConfig,
        Action<WizardConfigBuilder> mutate)
    {
        File.WriteAllText(paths.NetclawConfigPath,
            JsonSerializer.Serialize(initialConfig, Json.JsonDefaults.ConfigFile));

        var builder = new WizardConfigBuilder(paths);
        mutate(builder);
        builder.WriteConfigFile();

        return ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
    }
}
