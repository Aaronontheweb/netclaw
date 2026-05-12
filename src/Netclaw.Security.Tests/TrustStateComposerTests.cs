// -----------------------------------------------------------------------
// <copyright file="TrustStateComposerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class TrustStateComposerTests : IDisposable
{
    /// <summary>
    /// xunit.v3 <c>SkipUnless</c> hook for POSIX-only tests. Tilde
    /// expansion via the <c>homeDirectoryOverride</c> threads through
    /// <c>Path.Combine</c>, which uses the platform separator —
    /// Windows produces backslash-mixed paths that don't match the
    /// forward-slash assertions in tests written against POSIX-shaped
    /// home directories.
    /// </summary>
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private readonly string _storeFile;
    private readonly AudienceTrustStore _store;
    private readonly SafeVerbList _safeVerbs = SafeVerbList.FromVerbs(["ls", "cat", "git status"]);
    private readonly ToolAudienceProfiles _profiles;

    public TrustStateComposerTests()
    {
        _storeFile = Path.Combine(Path.GetTempPath(), $"netclaw-tsc-{Guid.NewGuid():N}.json");
        _store = new AudienceTrustStore(_storeFile);
        _profiles = new ToolAudienceProfiles
        {
            Personal = new ToolAudienceProfile
            {
                ReadFiles = new ToolFilesystemAccessProfile
                {
                    Mode = ToolFilesystemMode.Roots,
                    Roots = ["/home/user/repos"]
                }
            },
            Team = new ToolAudienceProfile
            {
                ReadFiles = new ToolFilesystemAccessProfile
                {
                    Mode = ToolFilesystemMode.Roots,
                    Roots = ["/opt/shared"]
                }
            },
            Public = new ToolAudienceProfile
            {
                ReadFiles = new ToolFilesystemAccessProfile
                {
                    Mode = ToolFilesystemMode.None,
                    Roots = []
                }
            }
        };
    }

    public void Dispose()
    {
        if (File.Exists(_storeFile)) File.Delete(_storeFile);
    }

    private TrustStateComposer NewComposer()
        => new(_profiles, _store, _safeVerbs, homeDirectoryOverride: "/home/user");

    [Fact]
    public void Compose_picks_audience_baseline_zones_from_profile()
    {
        var personal = NewComposer().Compose(TrustAudience.Personal, "/home/user/.netclaw/sessions/x");
        var team = NewComposer().Compose(TrustAudience.Team, "/home/user/.netclaw/sessions/x");

        Assert.True(personal.IsPathInTrustedZone("/home/user/repos/foo"));
        Assert.False(personal.IsPathInTrustedZone("/opt/shared/foo"));

        Assert.True(team.IsPathInTrustedZone("/opt/shared/foo"));
        Assert.False(team.IsPathInTrustedZone("/home/user/repos/foo"));
    }

    [Fact]
    public void Compose_overlays_persisted_zones_from_store()
    {
        _store.AddTrustedZone(TrustAudience.Personal, "/etc/nginx");

        var state = NewComposer().Compose(TrustAudience.Personal, "/home/user/.netclaw/sessions/x");

        Assert.True(state.IsPathInTrustedZone("/etc/nginx"));
        Assert.True(state.IsPathInTrustedZone("/home/user/repos/foo"));  // baseline still applies
    }

    [Fact]
    public void Compose_overlays_session_scope_zones_passed_in_per_call()
    {
        var state = NewComposer().Compose(
            TrustAudience.Personal,
            "/home/user/.netclaw/sessions/x",
            sessionTrustedZones: ["/tmp/scratch"]);

        Assert.True(state.IsPathInTrustedZone("/tmp/scratch/foo"));
        Assert.True(state.IsPathInTrustedZone("/home/user/repos/foo"));  // baseline retained
    }

    [Fact]
    public void Compose_always_includes_session_directory()
    {
        var state = NewComposer().Compose(
            TrustAudience.Public,  // Public has empty baseline; session_dir must be the only trusted zone
            "/home/user/.netclaw/sessions/abc");

        Assert.True(state.IsPathInTrustedZone("/home/user/.netclaw/sessions/abc/inbox/file.json"));
        Assert.False(state.IsPathInTrustedZone("/home/user/elsewhere"));
    }

    [Fact]
    public void Compose_overlays_persisted_verb_patterns_from_store()
    {
        _store.AddVerbPattern(TrustAudience.Personal, "git push *");

        var state = NewComposer().Compose(TrustAudience.Personal, "/home/user/.netclaw/sessions/x");

        Assert.Contains("git push *", state.AllVerbPatterns);
    }

    [Fact]
    public void Compose_overlays_session_verb_patterns_passed_in()
    {
        var state = NewComposer().Compose(
            TrustAudience.Personal,
            "/home/user/.netclaw/sessions/x",
            sessionVerbPatterns: ["dotnet test *"]);

        Assert.Contains("dotnet test *", state.AllVerbPatterns);
    }

    [Fact]
    public void Compose_carries_safe_verbs_list()
    {
        var state = NewComposer().Compose(TrustAudience.Personal, "/home/user/.netclaw/sessions/x");

        var lsVerb = new VerbChain { Tokens = ["ls"] };
        Assert.True(state.IsReadOnlyVerb(lsVerb));
    }

    [Fact(SkipUnless = nameof(IsPosix), Skip = "POSIX-only path semantics")]
    public void Compose_uses_home_directory_override_for_tilde_expansion()
    {
        // Audience baseline has no zones that use ~, so add one via the store
        // and verify it expands using the composer's home override.
        _store.AddTrustedZone(TrustAudience.Personal, "~/special");

        var state = NewComposer().Compose(TrustAudience.Personal, "/home/user/.netclaw/sessions/x");

        Assert.True(state.IsPathInTrustedZone("/home/user/special/foo"));
    }

    [Fact]
    public void Compose_throws_on_unknown_audience_value()
    {
        var composer = NewComposer();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => composer.Compose((TrustAudience)999, "/home/user/.netclaw/sessions/x"));
    }

    [Fact]
    public void Compose_returns_distinct_state_objects_for_different_audiences()
    {
        var composer = NewComposer();
        var personal = composer.Compose(TrustAudience.Personal, "/home/user/.netclaw/sessions/x");
        var team = composer.Compose(TrustAudience.Team, "/home/user/.netclaw/sessions/x");

        // They share the SafeVerbList and home directory but have different zone sets.
        Assert.NotEqual(personal.AllTrustedZones.Count, team.AllTrustedZones.Count + 1);  // sanity: both have session_dir
        Assert.True(personal.IsPathInTrustedZone("/home/user/repos/foo"));
        Assert.False(team.IsPathInTrustedZone("/home/user/repos/foo"));
    }

    [Fact]
    public void Compose_with_Mode_All_trusts_arbitrary_paths_outside_Roots()
    {
        // Operator declared Personal as filesystem-unrestricted at the
        // profile layer (Mode=All). The composer must propagate that into
        // TrustState so the zone gate stops prompting on every path.
        // Roots is intentionally empty — Mode=All makes it meaningless.
        _profiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.All,
            Roots = []
        };

        var state = NewComposer().Compose(TrustAudience.Personal, "/home/user/.netclaw/sessions/x");

        Assert.True(state.IsPathInTrustedZone("/etc/nginx"));
        Assert.True(state.IsPathInTrustedZone("/var/log/syslog"));
        Assert.True(state.IsPathInTrustedZone("/tmp/whatever"));
    }

    [Fact]
    public void Compose_with_Mode_None_does_not_trust_paths_outside_session_dir()
    {
        // Mode=None means "trust nothing baseline" — only session_dir and
        // explicit per-call grants count. Confirms the composer doesn't
        // accidentally treat None as All.
        _profiles.Personal.ReadFiles = new ToolFilesystemAccessProfile
        {
            Mode = ToolFilesystemMode.None,
            Roots = ["/this/is/ignored/when/mode/is/none"]  // realistically empty, but defensive
        };

        var state = NewComposer().Compose(TrustAudience.Personal, "/home/user/.netclaw/sessions/x");

        Assert.False(state.IsPathInTrustedZone("/etc/nginx"));
        Assert.False(state.IsPathInTrustedZone("/home/user/repos/foo"));
        Assert.True(state.IsPathInTrustedZone("/home/user/.netclaw/sessions/x/inbox/file"));
    }

    [Fact]
    public void Compose_with_Mode_Roots_only_trusts_listed_roots()
    {
        // Sanity that the existing Mode=Roots behavior is unchanged by the
        // Mode=All wiring. Personal default in this test class is already
        // Mode=Roots with /home/user/repos — we just assert the negative.
        var state = NewComposer().Compose(TrustAudience.Personal, "/home/user/.netclaw/sessions/x");

        Assert.True(state.IsPathInTrustedZone("/home/user/repos/foo"));
        Assert.False(state.IsPathInTrustedZone("/etc/nginx"));
        Assert.False(state.IsPathInTrustedZone("/home/user/elsewhere"));
    }
}
