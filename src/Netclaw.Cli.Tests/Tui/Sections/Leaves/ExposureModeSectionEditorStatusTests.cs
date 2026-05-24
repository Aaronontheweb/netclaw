// -----------------------------------------------------------------------
// <copyright file="ExposureModeSectionEditorStatusTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Sections.Leaves;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Sections.Leaves;

/// <summary>
/// Audit-finding regression (B4 second pass): GetStatus previously
/// returned <see cref="SectionStatus.Configured"/> in both branches of
/// a ternary, then in the fix returned <c>Configured</c> unconditionally
/// — both collapse the "operator has never touched this" signal. The
/// correct behavior surfaces the implicit-Local default as
/// <see cref="SectionStatus.NotConfigured"/> until an explicit Daemon
/// section is written.
/// </summary>
public sealed class ExposureModeSectionEditorStatusTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ExposureModeSectionEditorStatusTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void GetStatus_NoDaemonSection_ReportsNotConfigured()
    {
        var editor = new ExposureModeSectionEditor();
        var context = new SectionEditorContext(
            paths: _paths,
            config: new Dictionary<string, object>(),
            secretPresent: _ => false);

        Assert.Equal(SectionStatus.NotConfigured, editor.GetStatus(context));
    }

    [Fact]
    public void GetStatus_ExplicitDaemonSection_ReportsConfigured()
    {
        var editor = new ExposureModeSectionEditor();
        var context = new SectionEditorContext(
            paths: _paths,
            config: new Dictionary<string, object>
            {
                ["Daemon"] = new Dictionary<string, object>
                {
                    ["ExposureMode"] = "ReverseProxy",
                    ["Host"] = "0.0.0.0",
                },
            },
            secretPresent: _ => false);

        Assert.Equal(SectionStatus.Configured, editor.GetStatus(context));
    }
}
