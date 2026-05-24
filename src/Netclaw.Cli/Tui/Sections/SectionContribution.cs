// -----------------------------------------------------------------------
// <copyright file="SectionContribution.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Declarative description of the changes a leaf editor wants the semantic
/// merge writer to apply against on-disk state. Capturing the explicit
/// actions (rather than a fully built section dict) lets the writer
/// preserve unrelated fields and inactive values when only one leaf has
/// been touched.
/// </summary>
public sealed record SectionContribution
{
    /// <summary>Config field actions applied against <c>netclaw.json</c>.</summary>
    public IReadOnlyList<ConfigFieldAction> ConfigActions { get; init; } = [];

    /// <summary>Secret actions applied against <c>secrets.json</c>.</summary>
    public IReadOnlyList<SecretAction> SecretActions { get; init; } = [];

    /// <summary>Empty contribution. Useful for leaves that have nothing to write yet.</summary>
    public static SectionContribution Empty { get; } = new();
}

/// <summary>
/// Action applied to a single dotted path in the loaded config dictionary.
/// </summary>
/// <remarks>
/// <para><b>Dotted path grammar (MVP):</b></para>
/// <list type="bullet">
///   <item>The separator is the single character <c>'.'</c>.</item>
///   <item>Segments are case-sensitive and SHALL match the literal dictionary key
///     (e.g., <c>Daemon.ExposureMode</c>, <c>Providers.openai.Endpoint</c>).</item>
///   <item>Segments SHALL NOT themselves contain <c>'.'</c>. Editors targeting a
///     dictionary whose keys legitimately contain dots (e.g., a channel id like
///     <c>general.announce</c>) SHALL emit a <see cref="SetConfigValue"/> for the
///     parent object and rebuild the inner map, rather than addressing the child
///     via a dotted path.</item>
///   <item>Array indexing via <c>[i]</c> is NOT supported in MVP. Editors that
///     need to update an element of a collection SHALL replace the whole
///     collection at its parent path.</item>
///   <item>A missing path is distinct from a path whose value is JSON <c>null</c>:
///     <see cref="SetConfigValue"/> with <c>Value = null</c> writes <c>null</c>;
///     <see cref="RemoveConfigValue"/> removes the key entirely.</item>
/// </list>
/// </remarks>
public abstract record ConfigFieldAction
{
    /// <summary>Dotted path of the field this action targets. See remarks for grammar.</summary>
    public required string DottedPath { get; init; }
}

/// <summary>Replace the value at <see cref="ConfigFieldAction.DottedPath"/>.</summary>
public sealed record SetConfigValue : ConfigFieldAction
{
    /// <summary>New value. <c>null</c> writes JSON <c>null</c>; use <see cref="RemoveConfigValue"/> to delete the key.</summary>
    public required object? Value { get; init; }
}

/// <summary>Remove the key at <see cref="ConfigFieldAction.DottedPath"/> if present.</summary>
public sealed record RemoveConfigValue : ConfigFieldAction;

/// <summary>
/// Action applied to a single dotted path in <c>secrets.json</c>. The merge
/// writer encrypts the value at rest using the existing protector.
/// </summary>
public abstract record SecretAction
{
    /// <summary>Dotted path of the secret this action targets.</summary>
    public required string DottedPath { get; init; }
}

/// <summary>
/// Replace the secret at <see cref="SecretAction.DottedPath"/>. Construction
/// rejects empty / whitespace <see cref="Value"/> so leaves cannot silently
/// wipe stored secrets — use <see cref="KeepSecret"/> for blank-keep semantics
/// and <see cref="RemoveSecret"/> for explicit deletion.
/// </summary>
public sealed record SetSecret : SecretAction
{
    private readonly string _value = null!;

    /// <summary>New secret value. SHALL NOT be null, empty, or whitespace.</summary>
    public required string Value
    {
        get => _value;
        init
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "SetSecret.Value SHALL NOT be null, empty, or whitespace. " +
                    "Use KeepSecret for blank-keep semantics or RemoveSecret to delete.",
                    nameof(value));
            }

            _value = value;
        }
    }
}

/// <summary>
/// Preserve the existing secret at <see cref="SecretAction.DottedPath"/>.
/// Emitted when an editor's field was left blank and the operator clearly
/// signalled "leave blank to keep".
/// </summary>
public sealed record KeepSecret : SecretAction;

/// <summary>
/// Explicit deletion of the secret at <see cref="SecretAction.DottedPath"/>.
/// This is the only way a leaf editor can remove a stored secret —
/// blank submission alone SHALL NOT delete.
/// </summary>
public sealed record RemoveSecret : SecretAction;
