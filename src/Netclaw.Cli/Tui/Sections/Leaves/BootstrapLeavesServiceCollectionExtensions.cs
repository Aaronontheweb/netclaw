// -----------------------------------------------------------------------
// <copyright file="BootstrapLeavesServiceCollectionExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// Registers the four bootstrap leaf editors (Provider, Identity, Security
/// Posture, Enabled Features) so that the future <c>netclaw config</c>
/// dashboard and the menu registry audit can enumerate them by id. Other
/// leaves (Channels, Search, etc.) will be added by the next change as part
/// of the dashboard composition.
/// </summary>
public static class BootstrapLeavesServiceCollectionExtensions
{
    public static IServiceCollection AddBootstrapSectionEditors(this IServiceCollection services)
    {
        services.AddSectionEditor<ProviderSectionEditor>();
        services.AddSectionEditor<IdentitySectionEditor>();
        services.AddSectionEditor<SecurityPostureSectionEditor>();
        services.AddSectionEditor<EnabledFeaturesSectionEditor>();
        services.AddSectionEditorRegistry();
        return services;
    }
}
