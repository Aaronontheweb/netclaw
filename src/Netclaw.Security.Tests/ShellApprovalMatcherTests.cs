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
    public void ExtractCandidateVerbs_path_aware_verb_appends_first_argument()
    {
        var verbs = _matcher.ExtractCandidateVerbs(
            new ToolName("shell_execute"),
            Args("cat /home/user/.netclaw/logs/crash.log"));
        Assert.Single(verbs);
        Assert.Contains(
            verbs,
            v => v.Replace('\\', '/').Equals("cat /home/user/.netclaw/logs/crash.log", StringComparison.OrdinalIgnoreCase));
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
