// -----------------------------------------------------------------------
// <copyright file="MenuRegistryAuditTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Sections.Leaves;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Sections;

/// <summary>
/// Audit that every registered <see cref="ISectionEditor"/> has the
/// supporting test class and metadata required by the
/// <c>section-editor-abstraction</c> spec.
///
/// <para>Hidden / synthetic leaves (<see cref="ISectionEditor.ShowInMenu"/>
/// = <c>false</c>) are still subject to round-trip coverage but are exempt
/// from config-dashboard tape requirements per task 9.3.</para>
///
/// <para>Routed handoff entries (e.g., <c>Inference Providers → netclaw provider</c>
/// in the future config dashboard) are tested separately in the
/// netclaw-config-command change per task 9.4. They are NOT
/// <see cref="ISectionEditor"/> implementations and therefore do not
/// participate in this audit.</para>
/// </summary>
public sealed class MenuRegistryAuditTests
{
    private static SectionEditorRegistry BuildRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ProviderDescriptorRegistry([]));
        services.AddSingleton<IProviderProbe>(new FakeProviderProbe());
        services.AddBootstrapSectionEditors();
        return services.BuildServiceProvider().GetRequiredService<SectionEditorRegistry>();
    }

    [Fact]
    public void Audit_AllRegisteredLeaves_HaveRoundTripTestClass()
    {
        var registry = BuildRegistry();
        var testAssembly = typeof(MenuRegistryAuditTests).Assembly;

        var missing = new List<string>();
        foreach (var editor in registry.All)
        {
            var expectedName = editor.GetType().Name + "Tests";
            var testClass = testAssembly.GetTypes()
                .FirstOrDefault(t => t.Name == expectedName && IsRoundTripBase(t));

            if (testClass is null)
            {
                missing.Add($"{editor.SectionId} → expected class {expectedName} inheriting SectionEditorTestBase<>");
                continue;
            }

            // The base contract Facts run even on an empty subclass — they
            // assert nothing leaf-specific. Require the subclass to declare
            // at least one DeclaredOnly [Fact] beyond the inherited contract
            // so reviewers can see meaningful leaf coverage.
            var declaredFacts = testClass
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Count(m => m.GetCustomAttribute<FactAttribute>() is not null
                            || m.GetCustomAttribute<TheoryAttribute>() is not null);

            if (declaredFacts == 0)
            {
                missing.Add($"{editor.SectionId} → {expectedName} has no declared [Fact]/[Theory] beyond inherited contract");
            }
        }

        Assert.True(missing.Count == 0,
            $"Registered leaf editors missing leaf-specific round-trip coverage: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Audit_AllRegisteredLeaves_DeclareDoctorChecksOrJustifyNoChecks()
    {
        var registry = BuildRegistry();
        var noncompliant = new List<string>();

        foreach (var editor in registry.All)
        {
            if (editor.RelevantDoctorChecks.Count > 0)
            {
                foreach (var t in editor.RelevantDoctorChecks)
                {
                    if (!typeof(IDoctorCheck).IsAssignableFrom(t))
                        noncompliant.Add($"{editor.SectionId} → {t.FullName} is not IDoctorCheck");
                }
                continue;
            }

            var attr = editor.GetType().GetCustomAttribute<NoDoctorChecksAttribute>();
            if (attr is null || string.IsNullOrWhiteSpace(attr.Justification))
                noncompliant.Add($"{editor.SectionId} → no doctor checks and no [NoDoctorChecks(\"...\")] justification");
        }

        Assert.True(noncompliant.Count == 0,
            $"Registered leaf editors with missing doctor coverage: {string.Join(", ", noncompliant)}");
    }

    [Fact]
    public void Audit_HiddenLeaves_AreExemptFromMenuVisibility()
    {
        // ShowInMenu = false leaves still need round-trip tests (covered by the
        // first audit), but they SHALL NOT show up in MenuVisible.
        var registry = BuildRegistry();

        foreach (var editor in registry.All.Where(e => !e.ShowInMenu))
            Assert.DoesNotContain(editor, registry.MenuVisible);
    }

    [Fact]
    public void Audit_AllSectionIds_AreUnique()
    {
        // Constructor-time enforcement, but doubled-up here so the audit fails
        // loudly with a clearer message if it ever regresses.
        var registry = BuildRegistry();
        var ids = registry.All.Select(e => e.SectionId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Audit_SyntheticInitOwnedIds_StayHidden()
    {
        // Every leaf listed in SectionEditorExemptions.SyntheticInitOwnedIds
        // SHALL be registered with ShowInMenu = false.
        var registry = BuildRegistry();
        foreach (var sid in SectionEditorExemptions.SyntheticInitOwnedIds)
        {
            var editor = registry.Find(sid);
            if (editor is null) continue; // not registered in this composition
            Assert.False(editor.ShowInMenu,
                $"Section '{sid}' is listed as synthetic/init-owned but ShowInMenu is true.");
        }
    }

    private static bool IsRoundTripBase(Type t)
    {
        var current = t.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType
                && current.GetGenericTypeDefinition() == typeof(SectionEditorTestBase<>))
            {
                return true;
            }
            current = current.BaseType;
        }
        return false;
    }
}
