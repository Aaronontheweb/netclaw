// -----------------------------------------------------------------------
// <copyright file="BootstrapLeavesServiceCollectionExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// DI composition for all <see cref="ISectionEditor"/> leaves used by the
/// post-install <c>netclaw config</c> dashboard plus the four bootstrap
/// leaves (Provider, Identity, SecurityPosture, EnabledFeatures) the
/// section-editor-abstraction change refactored. The dashboard composer
/// pulls leaves out of <see cref="SectionEditorRegistry"/> by id rather
/// than depending on individual extension methods.
/// </summary>
public static class BootstrapLeavesServiceCollectionExtensions
{
    /// <summary>
    /// Register the four bootstrap leaves only. Useful for tests that
    /// want to scope the registry tightly. Production composition uses
    /// <see cref="AddAllSectionEditors"/>.
    /// </summary>
    public static IServiceCollection AddBootstrapSectionEditors(this IServiceCollection services)
    {
        services.AddSectionEditor<ProviderSectionEditor>();
        services.AddSectionEditor<IdentitySectionEditor>();
        services.AddSectionEditor<SecurityPostureSectionEditor>();
        services.AddSectionEditor<EnabledFeaturesSectionEditor>();
        services.AddSectionEditorRegistry();
        return services;
    }

    /// <summary>
    /// Register every leaf editor wired by this codebase — bootstrap
    /// leaves plus the channel / skill source / telemetry / standalone /
    /// security-and-access editors composed by the <c>netclaw config</c>
    /// dashboard. Idempotent across calls.
    /// </summary>
    public static IServiceCollection AddAllSectionEditors(this IServiceCollection services)
    {
        // Bootstrap leaves.
        services.AddSectionEditor<ProviderSectionEditor>();
        services.AddSectionEditor<IdentitySectionEditor>();

        // Security & Access.
        services.AddSectionEditor<SecurityPostureSectionEditor>();
        services.AddSectionEditor<EnabledFeaturesSectionEditor>();
        services.AddSectionEditor<AudienceProfilesSectionEditor>();
        services.AddSectionEditor<ExposureModeSectionEditor>();

        // Channels.
        services.AddSectionEditor<ChannelSlackSectionEditor>();
        services.AddSectionEditor<ChannelDiscordSectionEditor>();
        services.AddSectionEditor<ChannelMattermostSectionEditor>();

        // Skill Sources.
        services.AddSectionEditor<ExternalSkillsSectionEditor>();
        services.AddSectionEditor<SkillFeedsSectionEditor>();

        // Telemetry & Alerting.
        services.AddSectionEditor<TelemetrySectionEditor>();
        services.AddSectionEditor<OutboundWebhooksSectionEditor>();

        // Standalone.
        services.AddSectionEditor<SearchSectionEditor>();
        services.AddSectionEditor<BrowserAutomationSectionEditor>();
        services.AddSectionEditor<InboundWebhooksSectionEditor>();

        services.AddSectionEditorRegistry();
        return services;
    }
}
