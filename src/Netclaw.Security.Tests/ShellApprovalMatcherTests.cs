// -----------------------------------------------------------------------
// <copyright file="ShellApprovalMatcherTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellApprovalMatcherTests
{
    private readonly ShellApprovalMatcher _matcher = ShellApprovalMatcher.Instance;

    private static Dictionary<string, object?> Args(string command) => new() { ["Command"] = command };

    private static Dictionary<string, object?> Args(string command, string workingDirectory)
        => new()
        {
            ["Command"] = command,
            ["WorkingDirectory"] = workingDirectory
        };

    private static ApprovalEntry Verb(string verb) => new() { Verb = verb, Directory = null };
    private static ApprovalEntry InDir(string verb, string dir) => new() { Verb = verb, Directory = dir };

    [Fact]
    public void ExtractPatterns_simple_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args("git push origin main"));
        Assert.Single(patterns);
        Assert.Equal("git push origin main", patterns[0]);
    }

    [Fact]
    public void ExtractPatterns_compound_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"));
        Assert.Equal(3, patterns.Count);
        Assert.Contains("git add .", patterns);
        Assert.Contains("git commit -m fix", patterns);
        Assert.Contains("git push", patterns);
    }

    [Fact]
    public void ExtractPatterns_recurses_into_bash_c_wrapper()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args("bash -c \"git push --force\""));

        Assert.Single(patterns);
        Assert.Equal("git push --force", patterns[0]);
    }

    [Fact]
    public void ExtractPatterns_empty_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args(""));
        Assert.Empty(patterns);
    }

    [Fact]
    public void ExtractCandidateVerbs_collapses_to_verb_chains_only()
    {
        // Pure verb chains, no normalized commands or directory roots — the
        // v2 matcher leaves the directory half of approval pairs to the cwd.
        var verbs = _matcher.ExtractCandidateVerbs(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"));
        Assert.Equal(3, verbs.Count);
        Assert.Contains("git add", verbs);
        Assert.Contains("git commit", verbs);
        Assert.Contains("git push", verbs);
    }

    [Fact]
    public void ExtractCandidateVerbs_emits_command_head_only()
    {
        // v2.1 path-extraction: verb chain is the command head only.
        // The path argument is captured separately on
        // ExtractCandidates(...).Directory; see
        // ShellApprovalMatcherPathExtractionTests for the full coverage.
        var verbs = _matcher.ExtractCandidateVerbs(
            new ToolName("shell_execute"),
            Args("cat /home/user/.netclaw/logs/crash.log"));
        Assert.Single(verbs);
        Assert.Equal("cat", verbs[0]);
    }

    [Fact]
    public void IsApproved_global_wildcard_matches_anywhere()
    {
        var approved = new[] { Verb("git push"), Verb("git add"), Verb("git commit") };
        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"),
            approved,
            cwd: "/anywhere"));
    }

    [Fact]
    public void IsApproved_one_verb_unapproved_returns_false()
    {
        var approved = new[] { Verb("git add"), Verb("git push") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"),
            approved,
            cwd: null));
    }

    [Fact]
    public void IsApproved_folder_scoped_entry_matches_when_cwd_is_under_directory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(tempRoot, "sub");
        Directory.CreateDirectory(sub);
        try
        {
            // Use a non-path-aware verb so the candidate stays a pure verb
            // chain ("git status"); path-aware verbs (cat, grep, etc.) append
            // their first positional argument which would not match a bare
            // verb in the approved entry.
            var approved = new[] { InDir("git status", tempRoot) };
            Assert.True(_matcher.IsApproved(
                new ToolName("shell_execute"),
                Args("git status"),
                approved,
                cwd: sub));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void IsApproved_folder_scoped_entry_does_not_match_when_cwd_is_outside()
    {
        var approved = new[] { InDir("grep", "/home/user/repos/foo") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("grep error file.log"),
            approved,
            cwd: "/etc"));
    }

    [Fact]
    public void IsApproved_folder_scoped_entry_requires_concrete_cwd()
    {
        var approved = new[] { InDir("grep", "/home/user/repos/foo") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("grep error file.log"),
            approved,
            cwd: null));
    }

    [Fact]
    public void IsApproved_recurses_into_bash_c_wrapper()
    {
        var approved = new[] { Verb("git push") };
        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("bash -c \"git push --force\""),
            approved,
            cwd: null));
    }

    [Fact]
    public void FormatForDisplay_returns_command()
    {
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"), Args("git push origin main"));
        Assert.Equal("git push origin main", display);
    }

    [Fact]
    public void IsMessy_true_for_bash_control_flow()
    {
        Assert.True(_matcher.IsMessy(
            new ToolName("shell_execute"),
            Args("for pid in $(pgrep netclawd); do echo $pid; done")));
    }

    [Fact]
    public void IsMessy_false_for_well_formed_compound()
    {
        Assert.False(_matcher.IsMessy(
            new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push")));
    }

    [Fact]
    public void IsApproved_returns_false_for_messy_command_even_with_global_wildcards()
    {
        // Even if every conceivable verb is approved, a messy command never
        // auto-runs: the matcher cannot extract verb chains to evaluate, and
        // the prompt must offer Once/Deny only.
        var approved = new[] { Verb("for"), Verb("do"), Verb("done"), Verb("echo") };
        Assert.False(_matcher.IsApproved(
            new ToolName("shell_execute"),
            Args("for x in 1 2 3; do echo $x; done"),
            approved,
            cwd: null));
    }
}

/// <summary>
/// Path-extraction-aware matcher tests. The v2.1 design moves path arguments
/// out of the verb chain and into the candidate's directory half so future
/// calls in the same tree match a single persisted entry.
/// </summary>
public sealed class ShellApprovalMatcherPathExtractionTests
{
    private readonly ShellApprovalMatcher _matcher = ShellApprovalMatcher.Instance;

    private static Dictionary<string, object?> Args(string command) => new() { ["Command"] = command };

    [Fact]
    public void ExtractCandidates_strips_path_from_verb()
    {
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("find /home/petabridge -name X"));

        var c = Assert.Single(candidates);
        Assert.Equal("find", c.Verb);
        Assert.Equal("/home/petabridge", c.Directory);
    }

    [Fact]
    public void ExtractCandidates_applies_file_parent_rule()
    {
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("cat ~/.bashrc"));

        var c = Assert.Single(candidates);
        Assert.Equal("cat", c.Verb);
        // Path.GetDirectoryName drops the trailing separator.
        Assert.Equal("~", c.Directory);
    }

    [Fact]
    public void ExtractCandidates_no_path_returns_null_directory()
    {
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("git status"));

        var c = Assert.Single(candidates);
        Assert.Equal("git status", c.Verb);
        Assert.Null(c.Directory);
    }

    [Fact]
    public void ExtractCandidates_compound_command_extracts_per_clause()
    {
        var candidates = _matcher.ExtractCandidates(new ToolName("shell_execute"),
            Args("ls /repo && git status"));

        Assert.Equal(2, candidates.Count);
        Assert.Equal("ls", candidates[0].Verb);
        Assert.Equal("/repo", candidates[0].Directory);
        Assert.Equal("git status", candidates[1].Verb);
        Assert.Null(candidates[1].Directory);
    }

    [Fact]
    public void Matches_when_candidate_path_under_entry_directory()
    {
        // Folder-scoped trust compounds: an entry on /home/petabridge
        // covers any candidate whose path is under it.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "find",
            candidateDirectory: "/home/petabridge/.netclaw",
            cwd: null,
            approvedEntries: [new ApprovalEntry { Verb = "find", Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Matches_when_candidate_path_equals_entry_directory()
    {
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "find",
            candidateDirectory: "/home/petabridge",
            cwd: null,
            approvedEntries: [new ApprovalEntry { Verb = "find", Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Rejects_when_candidate_path_outside_entry_directory()
    {
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "find",
            candidateDirectory: "/home/other",
            cwd: null,
            approvedEntries: [new ApprovalEntry { Verb = "find", Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Falls_back_to_cwd_when_candidate_path_is_null()
    {
        // No path argument on the candidate — cwd is the effective directory.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "git status",
            candidateDirectory: null,
            cwd: "/home/petabridge/.netclaw",
            approvedEntries: [new ApprovalEntry { Verb = "git status", Directory = "/home/petabridge" }]));
    }

    [Fact]
    public void Null_directory_entry_matches_any_candidate()
    {
        // Global wildcard ignores both candidate path and cwd.
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidateVerb: "freshdesk",
            candidateDirectory: null,
            cwd: null,
            approvedEntries: [new ApprovalEntry { Verb = "freshdesk", Directory = null }]));
    }

    [Fact]
    public void IsPureSideEffect_skips_echo_without_redirect()
    {
        Assert.True(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("echo", Directory: null)));
    }

    [Fact]
    public void IsPureSideEffect_does_not_skip_echo_with_redirect_target()
    {
        // echo X > /tmp/log gets /tmp as its directory via the path-arg
        // scan, which means it's no longer "pure" side effect.
        Assert.False(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("echo", Directory: "/tmp")));
    }

    [Fact]
    public void IsPureSideEffect_does_not_skip_action_verbs()
    {
        Assert.False(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("find", Directory: null)));
        Assert.False(ApprovalPatternMatching.IsPureSideEffect(
            new ApprovalCandidate("git push", Directory: null)));
    }

    [Fact]
    public void ExtractCandidates_caps_echo_at_one_token()
    {
        // Without the SingleTokenSideEffectVerbs cap, the verb-chain
        // extractor would capture `echo hello` as a 2-token verb (since
        // `hello` neither starts with `-` nor matches LooksLikeArgument)
        // and the side-effect skip list would not match. Aaron's real
        // dogfood case used `echo "---REMOTE-INFO---"` which already
        // breaks at the leading `-` — but operators routinely run
        // `echo hello`-shape commands in build scripts.
        // (`echo done` would be the more obvious example but `done` is
        // a bash control-flow keyword and triggers IsMessyCompoundCommand,
        // which returns zero candidates.)
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?> { ["Command"] = "echo hello" });

        var c = Assert.Single(candidates);
        Assert.Equal("echo", c.Verb);
        Assert.True(ApprovalPatternMatching.IsPureSideEffect(c));
    }

    [Fact]
    public void ExtractCandidates_extracts_cd_target_as_directory()
    {
        // Aaron's dogfood case: `cd /repo && git remote -v && ...`. The
        // header / persistence layer needs the cd target as the candidate's
        // directory so the prompt can show the meaningful trust scope
        // rather than the per-session ephemeral session_dir.
        var candidates = _matcher.ExtractCandidates(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = "cd /home/petabridge/repositories/stannardlabs/netclaw && git remote -v"
            });

        Assert.Contains(candidates,
            c => c.Verb == "cd"
              && c.Directory == "/home/petabridge/repositories/stannardlabs/netclaw");
        // git remote has no path argument so its directory falls back to cwd
        // at match time (Directory == null on the candidate itself).
        Assert.Contains(candidates,
            c => c.Verb == "git remote" && c.Directory == null);
    }

    [Fact]
    public void IsApproved_treats_side_effect_candidates_as_authorized()
    {
        // Regression: when a compound command contains both action verbs and
        // pure side-effect clauses (echo "==="), persistence skips the echo
        // but the matcher historically did not. Result: after the user
        // clicked Always anywhere, the action verbs were stored but echo
        // wasn't, so the retry's authorization check saw echo as unapproved
        // and threw ToolApprovalRequiredException — which escaped the
        // already-active approval-pause catch and failed the turn.
        // This test asserts IsApproved skips side-effect candidates the
        // same way persistence does.
        var approvedEntries = new[]
        {
            new ApprovalEntry { Verb = "cd", Directory = null },
            new ApprovalEntry { Verb = "git status", Directory = null },
            new ApprovalEntry { Verb = "git remote", Directory = null }
            // No echo entry — exactly what the side-effect skip produces.
        };

        var compound =
            "cd ~/repo && git status && echo \"---\" && git remote -v && echo \"finished\"";
        Assert.True(_matcher.IsApproved(
            new ToolName("shell_execute"),
            new Dictionary<string, object?> { ["Command"] = compound },
            approvedEntries,
            cwd: null));
    }
}

public sealed class DefaultApprovalMatcherTests
{
    private readonly DefaultApprovalMatcher _matcher = DefaultApprovalMatcher.Instance;

    private static ApprovalEntry Verb(string verb) => new() { Verb = verb, Directory = null };

    [Fact]
    public void ExtractPatterns_returns_tool_name()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("mcp:memorizer:store"), null);
        Assert.Single(patterns);
        Assert.Equal("mcp:memorizer:store", patterns[0]);
    }

    [Fact]
    public void IsApproved_matches_exact_tool_name()
    {
        Assert.True(_matcher.IsApproved(
            new ToolName("mcp:memorizer:store"),
            null,
            [Verb("mcp:memorizer:store")],
            cwd: null));
    }

    [Fact]
    public void IsApproved_no_match()
    {
        Assert.False(_matcher.IsApproved(
            new ToolName("mcp:memorizer:store"),
            null,
            [Verb("mcp:memorizer:get")],
            cwd: null));
    }
}
