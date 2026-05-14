// -----------------------------------------------------------------------
// <copyright file="ProviderRenamer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Provider;

/// <summary>
/// Renames a provider entry in <c>netclaw.json</c> and
/// <c>.secrets/netclaw-secrets.json</c> by swapping the dictionary key.
/// Rename-only: does not migrate references in <c>Models.*.Provider</c>
/// or anywhere else.
/// </summary>
internal readonly record struct RenameResult(bool Success, string? ErrorMessage)
{
    public static RenameResult Ok() => new(true, null);
    public static RenameResult Fail(string message) => new(false, message);
}

internal static class ProviderRenamer
{
    /// <summary>
    /// Rename a provider in both config files.
    /// </summary>
    /// <remarks>
    /// Validation rules:
    /// <list type="bullet">
    /// <item><paramref name="oldName"/> must exist in <c>Providers</c> in <c>netclaw.json</c>.</item>
    /// <item><paramref name="newName"/> must be non-empty after trimming.</item>
    /// <item><paramref name="newName"/> must not collide (case-insensitive) with any other
    /// provider key already present in either file.</item>
    /// <item>A case-only change (e.g. <c>my-vllm</c> → <c>My-Vllm</c>) is permitted and rewrites
    /// the key in place.</item>
    /// </list>
    /// </remarks>
    public static RenameResult Rename(NetclawPaths paths, string oldName, string newName)
    {
        var trimmed = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return RenameResult.Fail("Provider name cannot be empty.");

        var (config, secrets) = ConfigFileHelper.LoadConfigFiles(paths);

        var providers = ConfigFileHelper.GetSectionOrNull(config, "Providers");
        if (providers is null || !providers.ContainsKey(oldName))
            return RenameResult.Fail($"Provider '{oldName}' not found.");

        // Collision check: walk both config and secrets dictionaries. A key
        // that case-insensitive-equals oldName is the entry we're renaming and
        // is not a collision. Any other key that case-insensitive-equals the
        // new name is a collision.
        if (HasCollision(providers, oldName, trimmed))
            return RenameResult.Fail($"A provider named '{trimmed}' already exists.");

        var secretProviders = ConfigFileHelper.GetSectionOrNull(secrets, "Providers");
        if (secretProviders is not null && HasCollision(secretProviders, oldName, trimmed))
            return RenameResult.Fail($"A provider named '{trimmed}' already exists in secrets.");

        var entry = providers[oldName];
        providers.Remove(oldName);
        providers[trimmed] = entry;
        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);

        if (secretProviders is not null && secretProviders.TryGetValue(oldName, out var secretEntry))
        {
            secretProviders.Remove(oldName);
            secretProviders[trimmed] = secretEntry;
            ConfigFileHelper.WriteSecretsFile(paths, secrets);
        }

        return RenameResult.Ok();
    }

    private static bool HasCollision(
        Dictionary<string, object> section, string oldName, string newName)
    {
        foreach (var key in section.Keys)
        {
            if (string.Equals(key, oldName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(key, newName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
