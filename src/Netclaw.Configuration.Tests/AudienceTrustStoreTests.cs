// -----------------------------------------------------------------------
// <copyright file="AudienceTrustStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class AudienceTrustStoreTests : IDisposable
{
    private readonly string _file;
    private readonly AudienceTrustStore _store;

    public AudienceTrustStoreTests()
    {
        _file = Path.Combine(Path.GetTempPath(), $"netclaw-trust-{Guid.NewGuid():N}.json");
        _store = new AudienceTrustStore(_file);
    }

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
        if (File.Exists(_store.LegacyQuarantinePath)) File.Delete(_store.LegacyQuarantinePath);
        if (File.Exists(_store.MalformedQuarantinePath)) File.Delete(_store.MalformedQuarantinePath);
    }

    // -------------------------------------------------------------------
    // Load: empty / missing / parsing
    // -------------------------------------------------------------------

    [Fact]
    public void Load_returns_empty_when_file_missing()
    {
        var data = _store.Load();
        Assert.Empty(data);
    }

    [Fact]
    public void Load_returns_empty_when_file_is_empty_object()
    {
        File.WriteAllText(_file, "{}");
        var data = _store.Load();
        Assert.Empty(data);
    }

    [Fact]
    public void Load_returns_persisted_state()
    {
        File.WriteAllText(_file, """
        {
          "personal": { "verbPatterns": ["git push *"], "trustedZones": ["/etc/nginx"] },
          "team": { "verbPatterns": [], "trustedZones": ["/opt/shared"] }
        }
        """);

        var data = _store.Load();

        Assert.Equal(2, data.Count);
        Assert.Contains("git push *", data["personal"].VerbPatterns);
        Assert.Contains("/etc/nginx", data["personal"].TrustedZones);
        Assert.Empty(data["team"].VerbPatterns);
        Assert.Contains("/opt/shared", data["team"].TrustedZones);
    }

    // -------------------------------------------------------------------
    // Legacy quarantine: v1 list and v2 versioned wrapper both archived
    // -------------------------------------------------------------------

    [Fact]
    public void Load_quarantines_v1_list_shape_to_legacy_bak()
    {
        File.WriteAllText(_file, """[{"verb":"git push","directory":null}]""");

        var data = _store.Load();

        Assert.Empty(data);
        Assert.False(File.Exists(_file), "legacy file should be moved aside");
        Assert.True(File.Exists(_store.LegacyQuarantinePath), "legacy file should appear in .v2-discarded.bak");
    }

    [Fact]
    public void Load_quarantines_v2_versioned_wrapper_to_legacy_bak()
    {
        File.WriteAllText(_file, """
        {
          "version": 2,
          "audiences": {
            "personal": { "shell_execute": [ {"verb": "git push", "directory": null} ] }
          }
        }
        """);

        var data = _store.Load();

        Assert.Empty(data);
        Assert.False(File.Exists(_file));
        Assert.True(File.Exists(_store.LegacyQuarantinePath));
    }

    [Fact]
    public void Load_quarantines_files_with_only_audiences_wrapper()
    {
        // Bare {audiences:...} without a version is also v2-shape and should
        // archive — the audiences wrapper itself is the marker.
        File.WriteAllText(_file, """{"audiences": {}}""");

        var data = _store.Load();

        Assert.Empty(data);
        Assert.True(File.Exists(_store.LegacyQuarantinePath));
    }

    [Fact]
    public void Load_quarantines_malformed_json_to_invalid_sibling()
    {
        File.WriteAllText(_file, "{not json");

        var data = _store.Load();

        Assert.Empty(data);
        Assert.False(File.Exists(_file));
        Assert.True(File.Exists(_store.MalformedQuarantinePath));
    }

    // -------------------------------------------------------------------
    // AddVerbPattern
    // -------------------------------------------------------------------

    [Fact]
    public void AddVerbPattern_persists_and_round_trips()
    {
        _store.AddVerbPattern(TrustAudience.Personal, "git push *");

        var roundTripped = new AudienceTrustStore(_file).Load();
        Assert.Contains("git push *", roundTripped["personal"].VerbPatterns);
    }

    [Fact]
    public void AddVerbPattern_is_idempotent_on_duplicates()
    {
        _store.AddVerbPattern(TrustAudience.Personal, "git push *");
        _store.AddVerbPattern(TrustAudience.Personal, "git push *");
        _store.AddVerbPattern(TrustAudience.Personal, "GIT PUSH *");

        var patterns = _store.GetVerbPatterns(TrustAudience.Personal);
        Assert.Single(patterns);
    }

    [Fact]
    public void AddVerbPattern_rejects_bare_verb_without_arg_glob()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => _store.AddVerbPattern(TrustAudience.Personal, "git push"));
        Assert.Contains("trailing arg-glob suffix", ex.Message);
    }

    [Fact]
    public void AddVerbPattern_rejects_empty_input()
    {
        Assert.Throws<ArgumentException>(
            () => _store.AddVerbPattern(TrustAudience.Personal, "   "));
    }

    [Fact]
    public void AddVerbPattern_accepts_specific_glob_in_args()
    {
        _store.AddVerbPattern(TrustAudience.Personal, "rm /tmp/*");
        Assert.Contains("rm /tmp/*", _store.GetVerbPatterns(TrustAudience.Personal));
    }

    // -------------------------------------------------------------------
    // AddTrustedZone
    // -------------------------------------------------------------------

    [Fact]
    public void AddTrustedZone_persists_and_round_trips()
    {
        _store.AddTrustedZone(TrustAudience.Team, "/opt/shared");

        var roundTripped = new AudienceTrustStore(_file).Load();
        Assert.Contains("/opt/shared", roundTripped["team"].TrustedZones);
    }

    [Fact]
    public void AddTrustedZone_normalizes_trailing_slash()
    {
        _store.AddTrustedZone(TrustAudience.Personal, "/etc/nginx/");
        _store.AddTrustedZone(TrustAudience.Personal, "/etc/nginx");

        var zones = _store.GetTrustedZones(TrustAudience.Personal);
        Assert.Single(zones);
        Assert.Equal("/etc/nginx", zones[0]);
    }

    [Fact]
    public void AddTrustedZone_is_idempotent_case_insensitive()
    {
        _store.AddTrustedZone(TrustAudience.Personal, "/Etc/Nginx");
        _store.AddTrustedZone(TrustAudience.Personal, "/etc/nginx");

        Assert.Single(_store.GetTrustedZones(TrustAudience.Personal));
    }

    [Fact]
    public void AddTrustedZone_rejects_empty_input()
    {
        Assert.Throws<ArgumentException>(
            () => _store.AddTrustedZone(TrustAudience.Personal, "   "));
    }

    // -------------------------------------------------------------------
    // RemoveVerbPattern / RemoveTrustedZone
    // -------------------------------------------------------------------

    [Fact]
    public void RemoveVerbPattern_returns_false_when_audience_absent()
    {
        Assert.False(_store.RemoveVerbPattern(TrustAudience.Personal, "git push *"));
    }

    [Fact]
    public void RemoveVerbPattern_removes_existing_entry()
    {
        _store.AddVerbPattern(TrustAudience.Personal, "git push *");
        _store.AddVerbPattern(TrustAudience.Personal, "rm /tmp/*");

        Assert.True(_store.RemoveVerbPattern(TrustAudience.Personal, "git push *"));

        var remaining = _store.GetVerbPatterns(TrustAudience.Personal);
        Assert.Single(remaining);
        Assert.Equal("rm /tmp/*", remaining[0]);
    }

    [Fact]
    public void RemoveVerbPattern_is_case_insensitive()
    {
        _store.AddVerbPattern(TrustAudience.Personal, "git push *");
        Assert.True(_store.RemoveVerbPattern(TrustAudience.Personal, "GIT PUSH *"));
        Assert.Empty(_store.GetVerbPatterns(TrustAudience.Personal));
    }

    [Fact]
    public void RemoveTrustedZone_removes_existing_entry()
    {
        _store.AddTrustedZone(TrustAudience.Personal, "/etc/nginx");
        Assert.True(_store.RemoveTrustedZone(TrustAudience.Personal, "/etc/nginx"));
        Assert.Empty(_store.GetTrustedZones(TrustAudience.Personal));
    }

    [Fact]
    public void RemoveTrustedZone_normalizes_trailing_slash_for_lookup()
    {
        _store.AddTrustedZone(TrustAudience.Personal, "/etc/nginx");
        Assert.True(_store.RemoveTrustedZone(TrustAudience.Personal, "/etc/nginx/"));
    }

    [Fact]
    public void Audience_cleaned_up_when_both_stores_empty()
    {
        _store.AddVerbPattern(TrustAudience.Personal, "git push *");
        _store.RemoveVerbPattern(TrustAudience.Personal, "git push *");

        var snapshot = _store.Snapshot();
        Assert.DoesNotContain("personal", snapshot.Keys);
    }

    [Fact]
    public void Audience_retained_when_other_store_still_populated()
    {
        _store.AddVerbPattern(TrustAudience.Personal, "git push *");
        _store.AddTrustedZone(TrustAudience.Personal, "/etc/nginx");
        _store.RemoveVerbPattern(TrustAudience.Personal, "git push *");

        var snapshot = _store.Snapshot();
        Assert.Contains("personal", snapshot.Keys);
        Assert.Contains("/etc/nginx", snapshot["personal"].TrustedZones);
    }

    // -------------------------------------------------------------------
    // Per-audience independence
    // -------------------------------------------------------------------

    [Fact]
    public void Stores_are_independent_per_audience()
    {
        _store.AddVerbPattern(TrustAudience.Personal, "git push *");
        _store.AddTrustedZone(TrustAudience.Team, "/opt/shared");

        Assert.Empty(_store.GetVerbPatterns(TrustAudience.Team));
        Assert.Empty(_store.GetTrustedZones(TrustAudience.Personal));
    }

    [Fact]
    public void Snapshot_returns_decoupled_copy()
    {
        _store.AddVerbPattern(TrustAudience.Personal, "git push *");
        var snapshot = _store.Snapshot();

        // Mutating the snapshot must not affect the underlying store.
        snapshot["personal"].VerbPatterns.Add("rm *");

        var fresh = _store.GetVerbPatterns(TrustAudience.Personal);
        Assert.Single(fresh);
        Assert.Equal("git push *", fresh[0]);
    }

    // -------------------------------------------------------------------
    // On-disk shape sanity check
    // -------------------------------------------------------------------

    [Fact]
    public void Save_emits_per_audience_top_level_keys_without_version_wrapper()
    {
        _store.AddVerbPattern(TrustAudience.Personal, "git push *");
        _store.AddTrustedZone(TrustAudience.Team, "/opt/shared");

        var json = File.ReadAllText(_file);

        // No version field, no audiences wrapper — the spec requires per-audience
        // top-level keys directly.
        Assert.DoesNotContain("\"version\"", json);
        Assert.DoesNotContain("\"audiences\"", json);
        Assert.Contains("\"personal\"", json);
        Assert.Contains("\"team\"", json);
        Assert.Contains("\"verbPatterns\"", json);
        Assert.Contains("\"trustedZones\"", json);
    }
}
