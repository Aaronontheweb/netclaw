// -----------------------------------------------------------------------
// <copyright file="ModelConfigurationValidation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>
/// Outcome of <see cref="ModelConfigurationValidation.Check"/>. When <see cref="Error"/> is set,
/// the other members hold empty defaults and must not be used.
/// </summary>
public sealed record ModelConfigurationCheck(
    string? Error,
    Dictionary<string, ProviderEntry> Providers,
    ModelSelection Models,
    ProviderRuntimeValidation Validation);

/// <summary>
/// The one validation of the Models section that the daemon runs at startup and that the config
/// watcher runs before it restarts the daemon. An invalid section is an operator error: startup
/// fails with <see cref="ModelConfigurationCheck.Error"/> and the watcher keeps the running config.
/// "No main model configured" is not an error. It stays the No-Op chat client outcome.
/// </summary>
public static class ModelConfigurationValidation
{
    public static ModelConfigurationCheck Check(IConfiguration configuration)
    {
        var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

        if (!ModelConfigurationResolver.TryResolve(configuration, out var resolution, out var error))
            return Failed(providers, error);

        var models = resolution.Selection;
        if (ValidateSelection(configuration, models) is { } selectionError)
            return Failed(providers, selectionError);

        var validation = ProviderRuntimeValidation.Evaluate(
            providers, models, ProviderRuntimeConfiguration.FromConfiguration(configuration));
        if (validation.Status == ProviderRuntimeStatus.Invalid)
        {
            return Failed(
                providers,
                $"Invalid model configuration: {validation.Reason}. Fix the Providers or Models section of netclaw.json.");
        }

        return new ModelConfigurationCheck(null, providers, models, validation);
    }

    /// <summary>
    /// Checks the values of the role-bound models. Under the current shape the message names the
    /// definition that the role selects, because that is the key the operator edits.
    /// </summary>
    public static string? ValidateSelection(IConfiguration configuration, ModelSelection models)
    {
        var result = new ModelSelectionValidator().Validate(null, models);
        if (!result.Failed)
            return null;

        var failures = result.Failures ?? [];
        var roles = configuration.GetSection("Models").GetSection(nameof(NamedModelConfiguration.Roles));
        return "Invalid model configuration: " + string.Join(" ", failures.Select(failure =>
        {
            foreach (var role in new[] { nameof(ModelSelection.Main), nameof(ModelSelection.Fallback), nameof(ModelSelection.Compaction) })
            {
                var definition = roles[role];
                if (!string.IsNullOrWhiteSpace(definition) && failure.StartsWith($"Models:{role}:", StringComparison.Ordinal))
                    return $"Models:Definitions:{definition}:" + failure[$"Models:{role}:".Length..];
            }

            return failure;
        }));
    }

    private static ModelConfigurationCheck Failed(Dictionary<string, ProviderEntry> providers, string error)
        => new(
            error,
            providers,
            new ModelSelection(),
            new ProviderRuntimeValidation(ProviderRuntimeStatus.Invalid, error, providers.Keys.ToList()));
}
