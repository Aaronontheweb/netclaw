// -----------------------------------------------------------------------
// <copyright file="SectionValidation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Cross-leaf pre-save validation contract from
/// <c>netclaw-config-command</c> §13. Every leaf editor that can produce
/// invalid output SHALL implement <see cref="IValidatesBeforeSave"/> and
/// return one of three outcomes:
/// <list type="bullet">
///   <item><see cref="SectionValidationKind.Valid"/>: save proceeds.</item>
///   <item><see cref="SectionValidationKind.StructuralError"/>: save is
///     blocked with no override.</item>
///   <item><see cref="SectionValidationKind.ProbeFailure"/>: save may
///     proceed if the operator confirms a <c>Save anyway</c> prompt.</item>
/// </list>
/// </summary>
public interface IValidatesBeforeSave
{
    /// <summary>
    /// Validate the prospective contribution. SHALL NOT mutate any state on
    /// disk — purely a read of in-memory editor state plus optional probes.
    /// </summary>
    Task<SectionValidationOutcome> ValidateAsync(
        SectionEditorContext context, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a pre-save validation pass.</summary>
public sealed record SectionValidationOutcome
{
    /// <summary>Classification of the outcome.</summary>
    public required SectionValidationKind Kind { get; init; }

    /// <summary>Human-readable explanation (operator-facing).</summary>
    public string? Message { get; init; }

    /// <summary>True when the operator may bypass via <c>Save anyway</c>.</summary>
    public bool CanSaveAnyway => Kind == SectionValidationKind.ProbeFailure;

    public static SectionValidationOutcome Valid { get; } =
        new() { Kind = SectionValidationKind.Valid };

    public static SectionValidationOutcome StructuralError(string message) =>
        new()
        {
            Kind = SectionValidationKind.StructuralError,
            Message = message,
        };

    public static SectionValidationOutcome ProbeFailure(string message) =>
        new()
        {
            Kind = SectionValidationKind.ProbeFailure,
            Message = message,
        };
}

/// <summary>Classification of a <see cref="SectionValidationOutcome"/>.</summary>
public enum SectionValidationKind
{
    /// <summary>Save proceeds without prompting.</summary>
    Valid,

    /// <summary>
    /// Structural / well-formedness error. Save is BLOCKED with no
    /// override per spec §13: "Structurally invalid config SHALL block
    /// save without override."
    /// </summary>
    StructuralError,

    /// <summary>
    /// Runtime or probe failure (unreachable host, missing binary,
    /// auth failure). Save MAY proceed if the operator explicitly
    /// confirms a <c>Save anyway</c> prompt per spec §13.
    /// </summary>
    ProbeFailure,
}
