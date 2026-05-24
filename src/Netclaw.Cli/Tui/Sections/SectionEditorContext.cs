// -----------------------------------------------------------------------
// <copyright file="SectionEditorContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Read-only context passed to <see cref="ISectionEditor.GetStatus"/> and
/// <see cref="ISectionEditor.Summary"/>. Carries a snapshot of the loaded
/// <c>netclaw.json</c> dictionary plus a secret-presence probe so leaves
/// can report configured/not-configured without rehydrating decrypted
/// secrets.
/// </summary>
public sealed class SectionEditorContext
{
    private readonly IReadOnlyDictionary<string, object> _config;

    public SectionEditorContext(
        NetclawPaths paths,
        IReadOnlyDictionary<string, object> config,
        Func<string, bool> secretPresent)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secretPresent);

        Paths = paths;
        _config = config;
        SecretPresent = secretPresent;
    }

    /// <summary>Canonical paths for the running install.</summary>
    public NetclawPaths Paths { get; }

    /// <summary>
    /// Snapshot of <c>netclaw.json</c> as a top-level read-only dictionary.
    /// Values MAY be nested dictionaries or <see cref="JsonElement"/> trees
    /// depending on whether the loader pre-flattened the tree; consumers
    /// SHOULD use <see cref="TryGetValue"/> for navigation rather than
    /// casting directly. The dictionary is a snapshot — mutating it has
    /// no effect on disk and SHALL NOT be relied upon.
    /// </summary>
    public IReadOnlyDictionary<string, object> Config => _config;

    /// <summary>
    /// Probe for whether a secret exists at a dotted path
    /// (e.g., <c>Providers.openai.ApiKey</c>). The probe MUST NOT decrypt
    /// the stored value — it only reports presence or absence. Path grammar
    /// matches <see cref="ConfigFieldAction"/> dotted paths.
    /// </summary>
    public Func<string, bool> SecretPresent { get; }

    /// <summary>
    /// Resolve a dotted path against the config snapshot. Returns
    /// <c>true</c> and the bound value if every segment exists; otherwise
    /// returns <c>false</c>. Path grammar matches
    /// <see cref="ConfigFieldAction"/> (case-sensitive segments, no array
    /// indexing, segments SHALL NOT contain <c>'.'</c>).
    /// </summary>
    public bool TryGetValue(string dottedPath, out object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dottedPath);

        var segments = dottedPath.Split('.');
        object? current = _config;

        foreach (var segment in segments)
        {
            current = current switch
            {
                IReadOnlyDictionary<string, object> ro when ro.TryGetValue(segment, out var v) => v,
                IDictionary<string, object> rw when rw.TryGetValue(segment, out var v) => v,
                JsonElement je when je.ValueKind == JsonValueKind.Object && je.TryGetProperty(segment, out var prop) => prop,
                _ => null,
            };

            if (current is null)
            {
                value = null;
                return false;
            }
        }

        value = current;
        return true;
    }
}
