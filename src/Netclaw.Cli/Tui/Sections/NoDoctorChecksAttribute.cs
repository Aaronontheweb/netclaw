// -----------------------------------------------------------------------
// <copyright file="NoDoctorChecksAttribute.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Declares — explicitly and with justification — that an
/// <see cref="ISectionEditor"/> intentionally has NO related doctor checks.
/// The menu registry audit consults this attribute when validating that
/// every registered leaf either declares <see cref="ISectionEditor.RelevantDoctorChecks"/>
/// or carries this attribute. Missing both fails the audit.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class NoDoctorChecksAttribute : Attribute
{
    public NoDoctorChecksAttribute(string justification)
    {
        if (string.IsNullOrWhiteSpace(justification))
            throw new ArgumentException(
                "A non-empty justification is required so reviewers can see why the leaf opts out of doctor coverage.",
                nameof(justification));

        Justification = justification;
    }

    /// <summary>Human-readable reason this leaf intentionally has no doctor checks.</summary>
    public string Justification { get; }
}
