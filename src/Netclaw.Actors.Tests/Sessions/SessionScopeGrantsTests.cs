// -----------------------------------------------------------------------
// <copyright file="SessionScopeGrantsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Trust-zones tasks 4.1-4.4: pins the in-memory-only contract for
/// session-scope grants on <see cref="LlmSessionActor"/>. Session scope
/// is the only approval scope that does NOT persist across actor
/// restarts; the structural test against <see cref="SessionSnapshot"/>
/// guards that boundary so a future refactor can't silently add a
/// persistence path.
/// </summary>
public sealed class SessionScopeGrantsTests
{
    [Fact]
    public void New_grants_object_starts_empty()
    {
        var grants = new SessionScopeGrants();

        Assert.Empty(grants.TrustedZones);
        Assert.Empty(grants.VerbPatterns);
    }

    [Fact]
    public void Add_trusted_zone_returns_true_for_first_insert_false_for_dup()
    {
        var grants = new SessionScopeGrants();

        Assert.True(grants.AddTrustedZone("/home/user/repos"));
        Assert.False(grants.AddTrustedZone("/home/user/repos"));
        Assert.Single(grants.TrustedZones);
    }

    [Fact]
    public void Add_verb_pattern_returns_true_for_first_insert_false_for_dup()
    {
        var grants = new SessionScopeGrants();

        Assert.True(grants.AddVerbPattern("git push origin main *"));
        Assert.False(grants.AddVerbPattern("git push origin main *"));
        Assert.Single(grants.VerbPatterns);
    }

    [Fact]
    public void Add_trims_surrounding_whitespace_before_dedupe()
    {
        var grants = new SessionScopeGrants();

        Assert.True(grants.AddTrustedZone("  /home/user/repos  "));
        Assert.False(grants.AddTrustedZone("/home/user/repos"));
        Assert.Single(grants.TrustedZones);
        Assert.Equal("/home/user/repos", Assert.Single(grants.TrustedZones));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Add_rejects_null_or_whitespace_only_input(string? input)
    {
        var grants = new SessionScopeGrants();

        Assert.False(grants.AddTrustedZone(input));
        Assert.False(grants.AddVerbPattern(input));
        Assert.Empty(grants.TrustedZones);
        Assert.Empty(grants.VerbPatterns);
    }

    [Fact]
    public void Verb_pattern_dedupe_is_case_insensitive()
    {
        // Verb chains and arg globs match case-insensitively in
        // ApprovalPatternMatching; the in-memory grants store mirrors
        // that contract so case-variant inputs don't bloat the set.
        var grants = new SessionScopeGrants();

        Assert.True(grants.AddVerbPattern("Git Push *"));
        Assert.False(grants.AddVerbPattern("git push *"));
        Assert.Single(grants.VerbPatterns);
    }

    [Fact]
    public void SessionSnapshot_does_not_expose_session_scope_grant_storage()
    {
        // Structural pin for tasks 4.3 + 4.4: session-scope grants must
        // never round-trip through the persisted snapshot. The snapshot
        // type has no field/property that could carry trustedZones or
        // session verbPatterns. If a future refactor tries to move
        // session-scope into SessionSnapshot (e.g. for "warm restart"
        // ergonomics), this test fails loudly and forces the design
        // conversation back through the trust-zones spec rather than
        // silently violating the in-memory-only invariant.
        var propertyNames = typeof(SessionSnapshot)
            .GetProperties()
            .Select(p => p.Name.ToLowerInvariant())
            .ToList();

        Assert.All(propertyNames, name =>
        {
            Assert.DoesNotContain("trustedzone", name);
            Assert.DoesNotContain("sessionverbpattern", name);
            Assert.DoesNotContain("sessionscopegrant", name);
        });
    }
}
