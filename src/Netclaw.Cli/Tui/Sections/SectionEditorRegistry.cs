// -----------------------------------------------------------------------
// <copyright file="SectionEditorRegistry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netclaw.Cli.Doctor;

namespace Netclaw.Cli.Tui.Sections;

/// <summary>
/// Registry of registered <see cref="ISectionEditor"/> leaves. The registry
/// is intentionally a FLAT leaf-editor registry — it does NOT define the
/// top-level <c>netclaw config</c> dashboard IA. The future config command
/// composes its own domain-oriented dashboard (with grouped pages and
/// routed handoffs to commands like <c>netclaw provider</c>) on top of this
/// registry. See <c>openspec/changes/section-editor-abstraction/proposal.md</c>
/// and <c>design.md</c> for the locked split between leaf registry and
/// dashboard shape.
/// </summary>
public sealed class SectionEditorRegistry
{
    private readonly Dictionary<string, ISectionEditor> _bySectionId;
    private readonly IReadOnlyList<ISectionEditor> _ordered;
    private readonly IReadOnlyList<ISectionEditor> _menuVisible;

    public SectionEditorRegistry(IEnumerable<ISectionEditor> editors)
    {
        ArgumentNullException.ThrowIfNull(editors);

        _bySectionId = new(StringComparer.Ordinal);
        var ordered = new List<ISectionEditor>();

        foreach (var editor in editors)
        {
            if (editor is null)
                throw new ArgumentException(
                    "Null section editor encountered during registry construction.",
                    nameof(editors));

            if (string.IsNullOrWhiteSpace(editor.SectionId))
                throw new InvalidOperationException(
                    $"Section editor of type '{editor.GetType().FullName}' produced a null or empty SectionId.");

            if (_bySectionId.TryGetValue(editor.SectionId, out var existing))
            {
                var newTypeName = editor.GetType().AssemblyQualifiedName;
                var existingTypeName = existing.GetType().AssemblyQualifiedName;

                var message = ReferenceEquals(editor.GetType(), existing.GetType())
                    ? $"Section editor type '{editor.GetType().FullName}' is registered twice " +
                      $"under SectionId '{editor.SectionId}'. Check the AddSectionEditor<{editor.GetType().Name}>() " +
                      "registrations — each leaf SHALL be registered exactly once."
                    : $"Duplicate SectionId '{editor.SectionId}': registered by both " +
                      $"'{existingTypeName}' and '{newTypeName}'. Section ids SHALL be unique across all editors.";

                throw new InvalidOperationException(message);
            }

            ValidateDoctorChecks(editor);

            _bySectionId.Add(editor.SectionId, editor);
            ordered.Add(editor);
        }

        _ordered = ordered;
        _menuVisible = ordered.Where(e => e.ShowInMenu).ToArray();
    }

    /// <summary>
    /// All registered leaf editors. Ordering follows DI registration order
    /// within a single composition root, which depends on the order of
    /// <c>AddSectionEditor</c> calls. Consumers that need a stable display
    /// order SHALL sort by <see cref="ISectionEditor.SectionId"/> or by a
    /// dashboard-defined category list.
    /// </summary>
    public IReadOnlyList<ISectionEditor> All => _ordered;

    /// <summary>
    /// Look up a leaf by stable id. Returns <c>null</c> when not registered.
    /// Throws <see cref="ArgumentException"/> on null/whitespace input — it
    /// is a programming error to probe with a missing id.
    /// </summary>
    public ISectionEditor? Find(string sectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);
        return _bySectionId.GetValueOrDefault(sectionId);
    }

    /// <summary>Look up a leaf by id; throws <see cref="KeyNotFoundException"/> when not registered.</summary>
    public ISectionEditor Get(string sectionId) =>
        Find(sectionId) ?? throw new KeyNotFoundException(
            $"No section editor registered with id '{sectionId}'.");

    /// <summary>
    /// Only leaves with <see cref="ISectionEditor.ShowInMenu"/> = true.
    /// Cached at construction so repeated dashboard reads do not allocate.
    /// </summary>
    public IReadOnlyList<ISectionEditor> MenuVisible => _menuVisible;

    private static void ValidateDoctorChecks(ISectionEditor editor)
    {
        if (editor.RelevantDoctorChecks is null)
            throw new InvalidOperationException(
                $"Section editor '{editor.SectionId}' ({editor.GetType().FullName}) returned null for RelevantDoctorChecks. " +
                "Return an empty list (with [NoDoctorChecks(\"...\")]) if no checks apply.");

        foreach (var t in editor.RelevantDoctorChecks)
        {
            if (t is null)
                throw new InvalidOperationException(
                    $"Section editor '{editor.SectionId}' ({editor.GetType().FullName}) included a null entry in RelevantDoctorChecks.");

            if (!typeof(IDoctorCheck).IsAssignableFrom(t))
                throw new InvalidOperationException(
                    $"Section editor '{editor.SectionId}' ({editor.GetType().FullName}) declared '{t.FullName}' " +
                    $"in RelevantDoctorChecks, but it does not implement {nameof(IDoctorCheck)}.");
        }
    }
}

/// <summary>
/// DI registration helpers for <see cref="ISectionEditor"/> implementations.
/// </summary>
public static class SectionEditorServiceCollectionExtensions
{
    /// <summary>
    /// Register a leaf editor as a singleton. Multiple distinct editor types
    /// compose into the singleton <see cref="SectionEditorRegistry"/> wired
    /// up by <see cref="AddSectionEditorRegistry"/>. Idempotent for the
    /// concrete <typeparamref name="TEditor"/> type so two composition
    /// modules registering the same editor will not cause a duplicate-id
    /// failure at registry construction.
    /// </summary>
    public static IServiceCollection AddSectionEditor<TEditor>(this IServiceCollection services)
        where TEditor : class, ISectionEditor
    {
        // TryAddEnumerable deduplicates the ISectionEditor registration by
        // (ServiceType=ISectionEditor, ImplementationType=TEditor), so the
        // same editor type registered twice still yields exactly one entry
        // in IEnumerable<ISectionEditor>.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ISectionEditor, TEditor>());

        return services;
    }

    /// <summary>
    /// Register the <see cref="SectionEditorRegistry"/> aggregate so callers
    /// can resolve all registered leaves by id. Idempotent across multiple calls.
    /// </summary>
    public static IServiceCollection AddSectionEditorRegistry(this IServiceCollection services)
    {
        services.TryAddSingleton<SectionEditorRegistry>(sp =>
            new SectionEditorRegistry(sp.GetServices<ISectionEditor>()));
        return services;
    }
}
