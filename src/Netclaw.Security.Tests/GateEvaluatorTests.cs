// -----------------------------------------------------------------------
// <copyright file="GateEvaluatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// End-to-end tests for the three-layer GateEvaluator. Exercises the live
/// ShellSyntaxTree parser plus the live ShellCommandPolicy plus the
/// composed TrustState to cover every scenario in the trust-zones spec.
/// </summary>
public sealed class GateEvaluatorTests
{
    // Use FromVerbs so tests are platform-independent — the bundled
    // safe-verbs files diverge between Linux and Windows but the
    // gate-evaluator logic doesn't change.
    private static readonly SafeVerbList LinuxSafeVerbs = SafeVerbList.FromVerbs(
    [
        "cd", "chdir", "pushd", "popd",
        "ls", "cat", "grep", "head", "tail", "find", "wc", "pwd",
        "git status", "git log", "git diff", "git show",
    ]);

    private static GateEvaluator NewEvaluator()
        => new(new ShellCommandPolicy(), new BashParser());

    private static TrustState NewTrustState(
        IEnumerable<string>? baselineZones = null,
        IEnumerable<string>? persistedZones = null,
        IEnumerable<string>? sessionZones = null,
        IEnumerable<string>? persistedVerbPatterns = null,
        IEnumerable<string>? sessionVerbPatterns = null,
        string sessionDirectory = "/home/user/.netclaw/sessions/test")
        => new(
            baselineZones ?? [],
            persistedZones ?? [],
            sessionZones ?? [],
            sessionDirectory,
            persistedVerbPatterns ?? [],
            sessionVerbPatterns ?? [],
            LinuxSafeVerbs,
            homeDirectory: "/home/user");

    // -------------------------------------------------------------------
    // Layer 1: hard-deny short-circuit
    // -------------------------------------------------------------------

    [Fact]
    public void HardDeny_short_circuits_overall_decision()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        var result = evaluator.Evaluate("netclaw daemon stop", TrustAudience.Personal, state);

        Assert.Equal(OverallGateDecision.HardDenied, result.OverallDecision);
        Assert.NotNull(result.HardDenyReason);
        Assert.Null(result.ZonePrompt);
        Assert.Null(result.VerbPrompt);
    }

    [Fact]
    public void HardDeny_in_second_clause_still_denies()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState(baselineZones: ["/home/user/repos"]);

        var result = evaluator.Evaluate(
            "cd /home/user/repos && netclaw daemon stop",
            TrustAudience.Personal,
            state);

        Assert.Equal(OverallGateDecision.HardDenied, result.OverallDecision);
    }

    [Fact]
    public void HardDeny_against_unparseable_still_evaluates()
    {
        // Unbalanced quote → IsUnparseable. Hard-deny still runs against
        // the raw source so this fork-bomb fragment still denies even
        // though the parser refused.
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        var result = evaluator.Evaluate(":(){:|:&};:", TrustAudience.Personal, state);

        Assert.Equal(OverallGateDecision.HardDenied, result.OverallDecision);
    }

    // -------------------------------------------------------------------
    // Layer 2 + 3 happy paths
    // -------------------------------------------------------------------

    [Fact]
    public void Read_only_verb_in_trusted_zone_runs_silently()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState(baselineZones: ["/home/user/repos"]);

        var result = evaluator.Evaluate(
            "ls /home/user/repos/foo",
            TrustAudience.Personal,
            state);

        Assert.Equal(OverallGateDecision.Approved, result.OverallDecision);
        Assert.Null(result.ZonePrompt);
        Assert.Null(result.VerbPrompt);
    }

    [Fact]
    public void Read_only_verb_outside_trusted_zone_prompts_zone_gate()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        var result = evaluator.Evaluate("cat /etc/hosts", TrustAudience.Personal, state);

        Assert.Equal(OverallGateDecision.NeedsPrompt, result.OverallDecision);
        Assert.NotNull(result.ZonePrompt);
        Assert.Contains("/etc/hosts", result.ZonePrompt.UntrustedPaths);
    }

    [Fact]
    public void Mutating_verb_in_trusted_zone_prompts_verb_gate_only()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState(baselineZones: ["/home/user/repos"]);

        var result = evaluator.Evaluate(
            "git push origin main",
            TrustAudience.Personal,
            state);

        Assert.Equal(OverallGateDecision.NeedsPrompt, result.OverallDecision);
        Assert.Null(result.ZonePrompt);   // no untrusted paths in this clause
        Assert.NotNull(result.VerbPrompt);
        Assert.Equal("git push *", result.VerbPrompt.VerbPattern);
    }

    [Fact]
    public void Mutating_verb_outside_trusted_zone_produces_both_prompts()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        var result = evaluator.Evaluate(
            "cp /etc/nginx/old.conf /etc/nginx/new.conf",
            TrustAudience.Personal,
            state);

        Assert.Equal(OverallGateDecision.NeedsPrompt, result.OverallDecision);
        Assert.NotNull(result.ZonePrompt);
        Assert.NotNull(result.VerbPrompt);
        Assert.Equal("cp *", result.VerbPrompt.VerbPattern);
    }

    // -------------------------------------------------------------------
    // Multi-path zone batching
    // -------------------------------------------------------------------

    [Fact]
    public void Multi_path_clause_unions_untrusted_paths_in_one_prompt()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        var result = evaluator.Evaluate(
            "cp /etc/foo /var/log/bar",
            TrustAudience.Personal,
            state);

        Assert.NotNull(result.ZonePrompt);
        Assert.Equal(2, result.ZonePrompt.UntrustedPaths.Count);
        Assert.Contains("/etc/foo", result.ZonePrompt.UntrustedPaths);
        Assert.Contains("/var/log/bar", result.ZonePrompt.UntrustedPaths);
    }

    [Fact]
    public void Untrusted_paths_dedupe_across_clauses()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        // cd attributes /etc to clause 2; clause 2 also explicitly operates
        // on /etc/hosts. The zone prompt should list /etc once, /etc/hosts
        // once — no duplicates from attribution arithmetic.
        var result = evaluator.Evaluate(
            "cd /etc && cat /etc/hosts",
            TrustAudience.Personal,
            state);

        Assert.NotNull(result.ZonePrompt);
        var distinct = result.ZonePrompt.UntrustedPaths.Distinct().Count();
        Assert.Equal(result.ZonePrompt.UntrustedPaths.Count, distinct);
    }

    // -------------------------------------------------------------------
    // cd-in-compound attribution
    // -------------------------------------------------------------------

    [Fact]
    public void Cd_attribution_propagates_target_as_path_in_subsequent_clause()
    {
        // cd /trusted-path && cat file.txt — the second clause operates on
        // /trusted-path via attribution; if /trusted-path is in zones,
        // the clause passes silently.
        var evaluator = NewEvaluator();
        var state = NewTrustState(baselineZones: ["/home/user/repos"]);

        var result = evaluator.Evaluate(
            "cd /home/user/repos && cat file.txt",
            TrustAudience.Personal,
            state);

        Assert.Equal(OverallGateDecision.Approved, result.OverallDecision);
    }

    [Fact]
    public void Cd_to_untrusted_dir_prompts_for_target_only()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        var result = evaluator.Evaluate("cd /foreign", TrustAudience.Personal, state);

        Assert.Equal(OverallGateDecision.NeedsPrompt, result.OverallDecision);
        Assert.NotNull(result.ZonePrompt);
        Assert.Contains("/foreign", result.ZonePrompt.UntrustedPaths);
        // cd is a read-only safe-verb but with untrusted path, the
        // read-only auto-pass doesn't kick in until the zone is approved.
        // For the call as it stands, no verb prompt is needed because
        // cd would still pass once zone is approved.
        Assert.Null(result.VerbPrompt);
    }

    // -------------------------------------------------------------------
    // Mixed-zone clause: one trusted + one untrusted path
    // -------------------------------------------------------------------

    [Fact]
    public void Mixed_zone_clause_with_read_only_verb_prompts_zone_only()
    {
        // grep -r foo /trusted /untrusted — read-only verb, mixed zone.
        // Per locked design: zone gate prompts for /untrusted; verb gate
        // auto-passes once zone is approved (mixed-zone rule).
        var evaluator = NewEvaluator();
        var state = NewTrustState(baselineZones: ["/home/user/repos"]);

        var result = evaluator.Evaluate(
            "grep -r foo /home/user/repos /etc",
            TrustAudience.Personal,
            state);

        Assert.Equal(OverallGateDecision.NeedsPrompt, result.OverallDecision);
        Assert.NotNull(result.ZonePrompt);
        Assert.Contains("/etc", result.ZonePrompt.UntrustedPaths);
        // No verb prompt because grep is read-only; the verb gate
        // auto-passes after zone gate approves the untrusted path.
        Assert.Null(result.VerbPrompt);
    }

    // -------------------------------------------------------------------
    // Pattern matching
    // -------------------------------------------------------------------

    [Fact]
    public void Persisted_verb_pattern_auto_passes_verb_gate()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState(
            baselineZones: ["/home/user/repos"],
            persistedVerbPatterns: ["git push *"]);

        var result = evaluator.Evaluate(
            "git push origin main",
            TrustAudience.Personal,
            state);

        Assert.Equal(OverallGateDecision.Approved, result.OverallDecision);
        Assert.Null(result.VerbPrompt);
    }

    [Fact]
    public void Session_verb_pattern_auto_passes_verb_gate()
    {
        // dotnet test with a path arg inside a trusted zone — both gates
        // pass: zone via baseline, verb via session pattern.
        var evaluator = NewEvaluator();
        var state = NewTrustState(
            baselineZones: ["/home/user/repos"],
            sessionVerbPatterns: ["dotnet test *"]);

        var result = evaluator.Evaluate(
            "dotnet test /home/user/repos/Foo.Tests",
            TrustAudience.Personal,
            state);

        Assert.Equal(OverallGateDecision.Approved, result.OverallDecision);
    }

    [Fact]
    public void Verb_pattern_does_not_match_different_verb()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState(
            baselineZones: ["/home/user/repos"],
            persistedVerbPatterns: ["git push *"]);

        var result = evaluator.Evaluate(
            "git pull origin main",
            TrustAudience.Personal,
            state);

        Assert.NotNull(result.VerbPrompt);
        Assert.Equal("git pull *", result.VerbPrompt.VerbPattern);
    }

    // -------------------------------------------------------------------
    // Session-scope zones
    // -------------------------------------------------------------------

    [Fact]
    public void Session_zone_grant_applies_for_subsequent_calls()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState(sessionZones: ["/tmp/scratch"]);

        var result = evaluator.Evaluate(
            "ls /tmp/scratch/foo",
            TrustAudience.Personal,
            state);

        Assert.Equal(OverallGateDecision.Approved, result.OverallDecision);
    }

    // -------------------------------------------------------------------
    // Empty-verb / redirect-only clauses
    // -------------------------------------------------------------------

    [Fact]
    public void Redirect_target_outside_zone_prompts_zone_only()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        var result = evaluator.Evaluate(
            "echo hello > /etc/dangerous.txt",
            TrustAudience.Personal,
            state);

        Assert.Equal(OverallGateDecision.NeedsPrompt, result.OverallDecision);
        Assert.NotNull(result.ZonePrompt);
        Assert.Contains("/etc/dangerous.txt", result.ZonePrompt.UntrustedPaths);
    }

    // -------------------------------------------------------------------
    // Dynamic-skip tokens
    // -------------------------------------------------------------------

    [Fact]
    public void Dynamic_skip_arg_is_excluded_from_zone_gate()
    {
        // $UNRESOLVED/foo cannot be statically resolved — Arg.Kind =
        // DynamicSkip. The zone gate excludes these so the clause is
        // treated as path-arg-less. The verb gate still applies normally.
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        var result = evaluator.Evaluate("ls $UNRESOLVED/foo", TrustAudience.Personal, state);

        // No untrusted path extracted (the only path arg was dynamic-skipped).
        // The verb is read-only, so with no untrusted paths the call passes.
        Assert.Null(result.ZonePrompt);
    }

    // -------------------------------------------------------------------
    // Unparseable safe-fail
    // -------------------------------------------------------------------

    [Fact]
    public void Unparseable_input_routes_to_safe_fail_zone_and_verb_prompts()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        var result = evaluator.Evaluate("echo \"unbalanced", TrustAudience.Personal, state);

        Assert.True(result.IsUnparseable);
        Assert.Equal(OverallGateDecision.NeedsPrompt, result.OverallDecision);
        Assert.NotNull(result.ZonePrompt);
        Assert.NotNull(result.VerbPrompt);
    }

    [Fact]
    public void Unparseable_carries_raw_command_into_zone_prompt()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        var result = evaluator.Evaluate("echo \"unbalanced", TrustAudience.Personal, state);

        Assert.NotNull(result.ZonePrompt);
        Assert.Contains("unbalanced", result.ZonePrompt.UntrustedPaths[0]);
    }

    // -------------------------------------------------------------------
    // Audience independence
    // -------------------------------------------------------------------

    [Fact]
    public void Different_audience_strings_surface_in_prompt_info()
    {
        var evaluator = NewEvaluator();
        var state = NewTrustState();

        var personalResult = evaluator.Evaluate("cat /etc/hosts", TrustAudience.Personal, state);
        var teamResult = evaluator.Evaluate("cat /etc/hosts", TrustAudience.Team, state);

        Assert.Equal(TrustAudience.Personal.ToWireValue(), personalResult.ZonePrompt?.Audience);
        Assert.Equal(TrustAudience.Team.ToWireValue(), teamResult.ZonePrompt?.Audience);
    }
}
