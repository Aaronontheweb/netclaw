// -----------------------------------------------------------------------
// <copyright file="UnimplementedLeafEditorsTests.cs" company="Petabridge, LLC">
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
/// Audit-finding regression: Telemetry / OutboundWebhooks /
/// InboundWebhooks editors previously returned WRONG step viewmodels
/// (ExternalSkillsStepViewModel, IdentityStepViewModel,
/// ExposureModeStepViewModel respectively). Selecting one in the
/// dashboard would mutate completely unrelated config sections on save.
/// The fix replaces the bogus returns with explicit
/// <see cref="NotImplementedException"/> so single-step hosting fails
/// loud instead of silently corrupting the wrong section.
/// </summary>
public sealed class UnimplementedLeafEditorsTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly WizardContext _wizardContext;

    public UnimplementedLeafEditorsTests()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        _wizardContext = new WizardContext
        {
            Paths = paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { },
        };
    }

    public void Dispose()
    {
        _wizardContext.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void TelemetrySectionEditor_CreateEditor_ThrowsLoudly()
    {
        var ex = Assert.Throws<NotImplementedException>(
            () => new TelemetrySectionEditor().CreateEditor(_wizardContext));
        Assert.Contains("Telemetry", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundWebhooksSectionEditor_CreateEditor_ThrowsLoudly()
    {
        var ex = Assert.Throws<NotImplementedException>(
            () => new OutboundWebhooksSectionEditor().CreateEditor(_wizardContext));
        Assert.Contains("Outbound Webhooks", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundWebhooksSectionEditor_CreateEditor_ThrowsLoudly()
    {
        var ex = Assert.Throws<NotImplementedException>(
            () => new InboundWebhooksSectionEditor().CreateEditor(_wizardContext));
        Assert.Contains("Inbound Webhooks", ex.Message, StringComparison.Ordinal);
    }
}
