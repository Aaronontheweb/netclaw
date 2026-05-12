// -----------------------------------------------------------------------
// <copyright file="ShellSyntaxTreeIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Security;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// Smoke tests confirming the ShellSyntaxTree contract Netclaw consumes is
/// stable across package upgrades. These are integration-level — they
/// exercise the live package without mocks — so an unexpected package
/// behavior change fails CI loudly before it surfaces in the gate evaluator.
///
/// When the gate evaluator lands, the parser-version-bump CI gate (task
/// 14.7 of approval-policy-trust-zones) runs the entire ShellSyntaxTree
/// corpus through Netclaw's live matcher; these tests are the smaller
/// per-PR canary that catches contract regressions earlier.
/// </summary>
public sealed class ShellSyntaxTreeIntegrationTests
{
    [Fact]
    public void Parser_resolves_through_DI_registration()
    {
        var services = new ServiceCollection();
        services.AddShellParser();

        using var provider = services.BuildServiceProvider();
        var parser = provider.GetRequiredService<IShellParser>();

        Assert.IsType<BashParser>(parser);
    }

    [Fact]
    public void Simple_verb_produces_single_clause()
    {
        var parser = new BashParser();

        var result = parser.Parse("ls -la /tmp");

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);

        var clause = result.Clauses[0];
        Assert.Equal(CompoundOperator.None, clause.Operator);
        Assert.Equal("ls", clause.Verb.Joined);
        Assert.Contains(clause.Args, a => a.Raw == "-la" && a.IsFlag);
        Assert.Contains(clause.Args, a => a.Raw == "/tmp" && a.IsPath);
    }

    [Fact]
    public void Multi_token_verb_collapses_with_BashArity()
    {
        var parser = new BashParser();

        var result = parser.Parse("git push origin main");

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);
        Assert.Equal("git push", result.Clauses[0].Verb.Joined);
    }

    [Fact]
    public void Compound_with_andif_produces_multiple_clauses()
    {
        var parser = new BashParser();

        var result = parser.Parse("cd /repo && git status");

        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);
        Assert.Equal(CompoundOperator.None, result.Clauses[0].Operator);
        Assert.Equal(CompoundOperator.AndIf, result.Clauses[1].Operator);
        Assert.Equal("cd", result.Clauses[0].Verb.Joined);
        Assert.Equal("git status", result.Clauses[1].Verb.Joined);
    }

    [Fact]
    public void Cd_in_compound_attributes_target_to_subsequent_clauses()
    {
        // The whole point of consuming ShellSyntaxTree: cd-in-compound
        // propagation lands on subsequent clauses so Netclaw's zone gate
        // sees /repo as a path the second clause operates on.
        var parser = new BashParser();

        var result = parser.Parse("cd /repo && cat file.txt");

        Assert.False(result.IsUnparseable);
        Assert.Equal(2, result.Clauses.Count);

        var secondClause = result.Clauses[1];
        Assert.Contains(secondClause.Args,
            a => a.IsCwdAttribution && a.Resolved == "/repo");
    }

    [Fact]
    public void Unparseable_input_sets_flag_without_throwing()
    {
        var parser = new BashParser();

        var result = parser.Parse("echo \"unbalanced");

        Assert.True(result.IsUnparseable);
        Assert.False(string.IsNullOrEmpty(result.UnparseableReason));
    }

    [Fact]
    public void Dynamic_token_marked_for_skip()
    {
        // Unresolved env var must be flagged so consumers don't extract
        // a literal "$UNRESOLVED/foo" as a path candidate.
        var parser = new BashParser();

        var result = parser.Parse("rm $UNRESOLVED/foo");

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);

        var argWithDynamic = result.Clauses[0].Args
            .FirstOrDefault(a => a.Raw.Contains("$UNRESOLVED"));
        Assert.NotNull(argWithDynamic);
        Assert.Equal(ArgKind.DynamicSkip, argWithDynamic.Kind);
        Assert.Null(argWithDynamic.Resolved);
    }

    [Fact]
    public void Leading_line_comment_is_stripped_from_clause_extraction()
    {
        // Regression test for ShellSyntaxTree #25 — bash line comments
        // (# starting a token, runs to end-of-line) must not appear as
        // verb-chain content. Pre-fix this surfaced as approval prompts
        // saying "Approve `# Get` in ..." and persistence-versus-recheck
        // verb-set mismatches that broke ApprovedSession on commented
        // commands. Fixed in ShellSyntaxTree 0.1.3-alpha.
        var parser = new BashParser();

        var result = parser.Parse(
            "# fetch the latest\ngit pull origin main");

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);
        Assert.Equal("git pull", result.Clauses[0].Verb.Joined);
    }

    [Fact]
    public void Hash_inside_double_quotes_is_not_a_comment()
    {
        // Per POSIX, # is only a comment when starting a word AND outside
        // quotes. echo "hash is #1234" should produce one verb (echo)
        // with one literal arg containing the hash sign.
        var parser = new BashParser();

        var result = parser.Parse("echo \"hash is #1234\"");

        Assert.False(result.IsUnparseable);
        Assert.Single(result.Clauses);
        Assert.Equal("echo", result.Clauses[0].Verb.Joined);
        Assert.Contains(result.Clauses[0].Args, a => a.Raw.Contains("#1234"));
    }
}
