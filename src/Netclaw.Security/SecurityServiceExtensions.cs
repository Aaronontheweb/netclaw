// -----------------------------------------------------------------------
// <copyright file="SecurityServiceExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Security.Skills;
using ShellSyntaxTree;

namespace Netclaw.Security;

public static class SecurityServiceExtensions
{
    /// <summary>
    /// Registers default security services.
    /// </summary>
    public static IServiceCollection AddContentSecurity(this IServiceCollection services)
    {
        services.AddSingleton<ContentPolicy>();
        services.AddSingleton<IContentScanner, MagicByteContentScanner>();
        services.AddSingleton<IPromptInjectionDetector, RegexPromptInjectionDetector>();
        services.AddSingleton<ISkillContentScanner, RegexSkillContentScanner>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="IShellParser"/> for the approval gate evaluator.
    /// The bash implementation is the only one shipped today; PowerShell and
    /// cmd parsers are deferred to ShellSyntaxTree v0.2+.
    /// </summary>
    public static IServiceCollection AddShellParser(this IServiceCollection services)
    {
        services.AddSingleton<IShellParser, BashParser>();
        return services;
    }
}
