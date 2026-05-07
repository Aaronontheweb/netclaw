// -----------------------------------------------------------------------
// <copyright file="ShellApprovalMatcherTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellApprovalMatcherTests
{
    private readonly ShellApprovalMatcher _matcher = ShellApprovalMatcher.Instance;

    public static TheoryData<string, string, bool> PlatformDirectoryMatchCases
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            if (OperatingSystem.IsWindows())
                data.Add(@"C:\Users\petabridge\.netclaw\logs\", @"type C:\Users\petabridge\.netclaw\logs\crash.log", true);
            else
                data.Add("/home/user/.netclaw/logs/", "cat /home/user/.netclaw/logs/crash.log", true);

            return data;
        }
    }

    private static Dictionary<string, object?> Args(string command) => new() { ["Command"] = command };

    private static Dictionary<string, object?> Args(string command, string workingDirectory)
        => new()
        {
            ["Command"] = command,
            ["WorkingDirectory"] = workingDirectory
        };

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
    public void ExtractPatterns_deduplicates()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"),
            Args("git push && git push --tags"));
        Assert.Equal(2, patterns.Count);
        Assert.Contains("git push", patterns);
        Assert.Contains("git push --tags", patterns);
    }

    [Fact]
    public void ExtractPatterns_recurses_into_bash_c_wrapper()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args("bash -c \"git push --force\""));

        Assert.Single(patterns);
        Assert.Equal("git push --force", patterns[0]);
    }

    [Fact]
    public void ExtractPatterns_batches_outer_and_inner_segments()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args("echo ok && bash -c \"git push --force\""));

        Assert.Equal(2, patterns.Count);
        Assert.Contains("echo ok", patterns);
        Assert.Contains("git push --force", patterns);
    }

    [Fact]
    public void ExtractPatterns_empty_command()
    {
        var patterns = _matcher.ExtractPatterns(new ToolName("shell_execute"), Args(""));
        Assert.Empty(patterns);
    }

    [Fact]
    public void IsApproved_all_patterns_approved()
    {
        var approved = new[] { "git add .", "git commit -m fix", "git push" };
        Assert.True(_matcher.IsApproved(new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"), approved));
    }

    [Fact]
    public void IsApproved_one_pattern_unapproved()
    {
        var approved = new[] { "git add .", "git push" };
        Assert.False(_matcher.IsApproved(new ToolName("shell_execute"),
            Args("git add . && git commit -m fix && git push"), approved));
    }

    [Theory]
    [InlineData("git push", "git push", true)]
    [InlineData("git push", "git push origin main", false)]
    [InlineData("gh", "gh --help", false)]
    [InlineData("/home/user/.netclaw/logs/", "grep timeout /home/user/.netclaw/logs/crash.log", true)]
    [InlineData("/home/user/.netclaw/logs/", "cat /home/user/.netclaw/config/secret.json", false)]
    public void IsApproved_pattern_matching(string pattern, string command, bool expected)
    {
        var approved = new[] { pattern };
        Assert.Equal(expected, _matcher.IsApproved(new ToolName("shell_execute"), Args(command), approved));
    }

    [Theory]
    [MemberData(nameof(PlatformDirectoryMatchCases))]
    public void IsApproved_platform_specific_directory_root_matching(string pattern, string command, bool expected)
    {
        var approved = new[] { pattern };
        Assert.Equal(expected, _matcher.IsApproved(new ToolName("shell_execute"), Args(command), approved));
    }

    [Fact]
    public void IsApproved_recurses_into_bash_c_wrapper()
    {
        var approved = new[] { "git push --force" };

        Assert.True(_matcher.IsApproved(new ToolName("shell_execute"),
            Args("bash -c \"git push --force\""), approved));
    }

    [Fact]
    public void ExtractPatterns_normalize_paths_but_keep_full_unit_shape()
    {
        var patterns = _matcher.ExtractPatterns(
            new ToolName("shell_execute"),
            Args("cat /etc/hosts && git push origin main"));
        Assert.Equal(2, patterns.Count);
        Assert.Contains("cat /etc/hosts", patterns);
        Assert.Contains("git push origin main", patterns);
    }

    [Fact]
    public void ExtractPatterns_keep_pipeline_together_as_one_unit()
    {
        var patterns = _matcher.ExtractPatterns(
            new ToolName("shell_execute"),
            Args("cat /var/log/syslog | grep error"));
        Assert.Single(patterns);
        Assert.Equal("cat /var/log/syslog | grep error", patterns[0]);
    }

    [Fact]
    public void FormatForDisplay_returns_command()
    {
        var display = _matcher.FormatForDisplay(new ToolName("shell_execute"), Args("git push origin main"));
        Assert.Equal("git push origin main", display);
    }

    // ── ExtractDirectoryRoots / ExtractApprovalEntries ──

    [Theory]
    [MemberData(nameof(PlatformDirectoryMatchCases))]
    public void ExtractDirectoryRoots_simple_path_command(string expectedRoot, string command, bool _)
    {
        var roots = _matcher.ExtractDirectoryRoots(
            new ToolName("shell_execute"),
            Args(command));
        Assert.Single(roots);
        Assert.Equal(expectedRoot, roots[0].ComparisonRoot);
    }

    [Fact]
    public void ExtractDirectoryRoots_pipeline_and_multiple_verbs_share_same_root()
    {
        var roots = _matcher.ExtractDirectoryRoots(
            new ToolName("shell_execute"),
            Args("grep 'error' /home/user/.netclaw/logs/app.log | wc -l"));
        Assert.Single(roots);
        Assert.Equal("/home/user/.netclaw/logs/", roots[0].ComparisonRoot.Replace('\\', '/'));
    }

    [Fact]
    public void ExtractDirectoryRoots_returns_empty_when_no_reusable_roots_exist()
    {
        var roots = _matcher.ExtractDirectoryRoots(
            new ToolName("shell_execute"),
            Args("git push origin main"));
        Assert.Empty(roots);
    }

    [Fact]
    public void ExtractApprovalEntries_use_roots_when_available()
    {
        var entries = _matcher.ExtractApprovalEntries(
            new ToolName("shell_execute"),
            Args("grep 'error' /home/user/.netclaw/logs/app.log | wc -l"));

        Assert.Single(entries);
        Assert.Equal("/home/user/.netclaw/logs/", entries[0].Replace('\\', '/'));
    }

    [Fact]
    public void ExtractApprovalEntries_fall_back_to_exact_unit_when_no_roots_exist()
    {
        var entries = _matcher.ExtractApprovalEntries(
            new ToolName("shell_execute"),
            Args("cat /home/user/.netclaw/logs/crash.log && git push origin main"));
        Assert.Equal(2, entries.Count);
        Assert.Contains("/home/user/.netclaw/logs/", entries.Select(p => p.Replace('\\', '/')));
        Assert.Contains("git push origin main", entries);
    }
}

public sealed class DefaultApprovalMatcherTests
{
    private readonly DefaultApprovalMatcher _matcher = DefaultApprovalMatcher.Instance;

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
        Assert.True(_matcher.IsApproved(new ToolName("mcp:memorizer:store"), null, ["mcp:memorizer:store"]));
    }

    [Fact]
    public void IsApproved_no_match()
    {
        Assert.False(_matcher.IsApproved(new ToolName("mcp:memorizer:store"), null, ["mcp:memorizer:get"]));
    }
}
