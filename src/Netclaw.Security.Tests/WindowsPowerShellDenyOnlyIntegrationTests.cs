// -----------------------------------------------------------------------
// <copyright file="WindowsPowerShellDenyOnlyIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class WindowsPowerShellDenyOnlyIntegrationTests
{
    private static readonly ShellExecutionEnvironment Environment =
        ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            PwshDialect.WindowsPowerShell51);
    private static readonly ShellExecutionEnvironment PowerShell7Environment =
        ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);

    [Theory]
    [InlineData("Get-ChildItem | ForEach-Object { netclaw daemon stop; $item++ }")]
    [InlineData("Get-ChildItem | ForEach-Object { $item++; netclaw daemon stop }")]
    [InlineData("Invoke-Custom { netclaw daemon stop; $item++ }")]
    [InlineData("Invoke-Custom { $item++; netclaw daemon stop }")]
    public void Default_deny_survives_a_mutation_before_or_after_it(string source)
    {
        var analysis = AssertDenyOnlyAnalysis(source);

        var decision = new ShellCommandPolicy(Environment).Evaluate(analysis);

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SelfDestructive, decision.DenyCategory);
    }

    [Theory]
    [InlineData("Write-Output input | ForEach-Object { Remove-Item -Path C:\\ -Recurse:$true; $item++ }")]
    [InlineData("Write-Output input | ForEach-Object { Remove-Item -Path C:\\ -Recurse:$flag; $item++ }")]
    public void Dynamic_inline_recurse_value_does_not_hide_a_nested_root_removal(string source)
    {
        var analysis = AssertDenyOnlyAnalysis(source);

        var decision = new ShellCommandPolicy(Environment).Evaluate(analysis);

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SystemDestructive, decision.DenyCategory);
    }

    [Theory]
    [InlineData("Start-Process pwsh -Verb $verb; $item++")]
    [InlineData("Start-Process pwsh -Verb \"Run$part\"; $item++")]
    [InlineData("custom-tool $operation delete; $item++")]
    public void Dynamic_arguments_do_not_create_static_deny_facts(string source)
    {
        var analysis = AssertDenyOnlyAnalysis(source);
        var policy = new ShellCommandPolicy(
            Environment,
            additionalDenyPatterns: ["custom-tool delete"]);

        var decision = policy.EvaluateDenyOnlyClauses(analysis.DenyOnlyClauses);

        Assert.True(decision.Allowed);
    }

    [Theory]
    [InlineData("Write-Output 'netclaw daemon stop'; $item++")]
    [InlineData("Write-Output \"netclaw daemon stop\"; $item++")]
    [InlineData("Write-Output ok; $item++; # netclaw daemon stop")]
    public void Quoted_data_and_comments_do_not_create_deny_facts(string source)
    {
        var analysis = AssertDenyOnlyAnalysis(source);

        var decision = new ShellCommandPolicy(Environment).Evaluate(analysis);

        Assert.True(decision.Allowed);
    }

    [Theory]
    [InlineData("net`claw daemon stop; $item++")]
    [InlineData("& 'netclaw' daemon stop; $item++")]
    public void Parser_decoded_static_heads_remain_deny_facts(string source)
    {
        var analysis = AssertDenyOnlyAnalysis(source);

        var decision = new ShellCommandPolicy(Environment).Evaluate(analysis);

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SelfDestructive, decision.DenyCategory);
    }

    [Fact]
    public void Decoded_wrapper_retains_a_nested_deny_with_null_source_spans()
    {
        const string source =
            "powershell.exe -Command 'netclaw daemon stop; $item++'";
        var analysis = AssertDenyOnlyAnalysis(source);

        var decision = new ShellCommandPolicy(Environment).Evaluate(analysis);

        Assert.Contains(
            analysis.DenyOnlyClauses,
            clause => clause.IsCommandStringWrapped);
        Assert.False(decision.Allowed);
    }

    [Theory]
    [InlineData(
        PwshDialect.PowerShell7,
        "Write-Output { netclaw daemon stop }; "
        + "Invoke-Command -ScriptBlock { Get-Date } -AsJob")]
    [InlineData(
        PwshDialect.WindowsPowerShell51,
        "Get-ChildItem | ForEach-Object { "
        + "Write-Output { netclaw daemon stop }; $_.Delete() }")]
    public void Non_diagnostic_failure_does_not_publish_inert_script_block_data(
        PwshDialect dialect,
        string source)
    {
        var environment = dialect == PwshDialect.PowerShell7
            ? PowerShell7Environment
            : Environment;
        var parsed = environment.Parse(source, @"C:\WORK");
        var analysis = new ShellCommandAnalyzer(environment).Analyze(
            source,
            @"C:\WORK");

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(analysis.Commands);
        Assert.Empty(analysis.DenyOnlyClauses);
        Assert.True(new ShellCommandPolicy(environment).Evaluate(analysis).Allowed);
    }

    [Fact]
    public void Deny_only_analysis_never_creates_approval_candidates()
    {
        const string source =
            "Get-ChildItem | ForEach-Object { Write-Output ok; $item++ }";
        var analysis = AssertDenyOnlyAnalysis(source);
        var approval = new ShellApprovalMatcher(Environment).AnalyzeInvocation(
            new ToolName("shell_execute"),
            new Dictionary<string, object?>
            {
                ["Command"] = source,
                ["WorkingDirectory"] = @"C:\WORK"
            });

        Assert.False(analysis.IsResolved);
        Assert.True(approval.IsMessy);
        Assert.Empty(approval.Patterns);
        Assert.Empty(approval.Candidates);
    }

    private static ShellCommandAnalysis AssertDenyOnlyAnalysis(string source)
    {
        var parsed = Environment.Parse(source, @"C:\WORK");
        var analysis = new ShellCommandAnalyzer(Environment).Analyze(
            source,
            @"C:\WORK");

        Assert.True(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(ShellAnalysisFailure.Unresolved, analysis.Failure);
        Assert.Empty(analysis.Commands);
        Assert.NotEmpty(analysis.DenyOnlyClauses);
        return analysis;
    }
}
