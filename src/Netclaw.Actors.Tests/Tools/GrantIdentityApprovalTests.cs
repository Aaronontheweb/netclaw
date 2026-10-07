// -----------------------------------------------------------------------
// <copyright file="GrantIdentityApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// One grant identity: the verb that the prompt shows is the phrase of the
/// command words that the answer saves and that a grant matches. Before this
/// rule, <c>pipedrive dealFields list</c> showed <c>pipedrive</c> and saved
/// <c>pipedrive dealFields list</c>, so the next <c>pipedrive</c> command
/// showed the same verb and prompted again.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class GrantIdentityApprovalTests(ShellApprovalMatrixFixture fixture)
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ToolName Shell = new(ShellTool.ToolName);

    // The session of each ShellApprovalHarness call.
    private static readonly ToolApprovalSessionId InvocationSession = (ToolApprovalSessionId)"signalr/approval-matrix";

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData(GrantScopeKind.Session)]
    [InlineData(GrantScopeKind.Folder)]
    [InlineData(GrantScopeKind.Everywhere)]
    public async Task Saved_grant_covers_the_command_that_saved_it(GrantScopeKind scope)
    {
        await using var harness = await CreateHarnessAsync();
        const string first = "pipedrive dealFields list --custom-only --json | jq '.[] | .name'";

        var prompt = await harness.EvaluateShellAsync(first, Ct);
        Assert.Equal(ApprovalOutcome.RequiresApproval, prompt.Outcome);
        Assert.Contains("pipedrive dealFields list", prompt.Prompt!.CandidateVerbs);
        Assert.DoesNotContain("pipedrive", prompt.Prompt.CandidateVerbs);

        var saved = await AnswerAsync(harness, first, scope);

        Assert.Contains(saved, static grant => grant.Candidate.VerbTokens!.SequenceEqual(["pipedrive", "dealFields", "list"]));
        await AssertAllowedByStoredGrantAsync(harness, first);
        await AssertAllowedByStoredGrantAsync(harness, "pipedrive dealFields list --json");

        // Negative controls: another verb of the program, another word case,
        // and the bare program keep the prompt. The prompt names each verb.
        await AssertPromptsForAsync(harness, "pipedrive organizationFields list --json", "pipedrive organizationFields list");
        await AssertPromptsForAsync(harness, "pipedrive deals delete 42", "pipedrive deals delete");
        await AssertPromptsForAsync(harness, "pipedrive dealfields list", "pipedrive dealfields list");
        await AssertPromptsForAsync(harness, "pipedrive", "pipedrive");
    }

    // SECURITY: a program-only grant stays exact. A store can hold such a grant
    // from an answer to the bare program. The rule does not make it wider.
    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    public async Task Program_only_grant_does_not_cover_a_verb_of_the_program()
    {
        await using var harness = await CreateHarnessAsync();
        harness.AddStoredShellEntry(
            TrustAudience.Personal,
            ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["pipedrive"]));

        await AssertAllowedByStoredGrantAsync(harness, "pipedrive");
        await AssertPromptsForAsync(harness, "pipedrive dealFields list --json", "pipedrive dealFields list");
        await AssertPromptsForAsync(harness, "pipedrive deals delete 42", "pipedrive deals delete");
    }

    [SlopwatchSuppress("SW001", "The Bash cases require a POSIX host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The Bash cases require a POSIX host.")]
    [InlineData("mytool subCommand list", "mytool subCommand list", "mytool otherCommand list")]
    [InlineData("aws s3api listObjects --bucket b", "aws listObjects", "aws s3api deleteObjects --bucket b")]
    [InlineData("pipedrive deals list", "pipedrive deals list", "pipedrive deals delete 42")]
    public async Task Prompt_verb_is_the_saved_grant_and_covers_the_next_call(
        string command,
        string shownVerb,
        string otherCommand)
    {
        await using var harness = await CreateHarnessAsync();

        await AssertPromptsForAsync(harness, command, shownVerb);
        var saved = await AnswerAsync(harness, command, GrantScopeKind.Session);

        Assert.Equal(shownVerb, ShellCommandWordText.FormatPhrase(ApprovalShell.Bash, Assert.Single(saved).Candidate.VerbTokens!));
        await AssertAllowedByStoredGrantAsync(harness, command);
        var other = await harness.EvaluateShellAsync(otherCommand, Ct);
        Assert.Equal(ApprovalOutcome.RequiresApproval, other.Outcome);
    }

    /// <summary>
    /// For each catalog command, the grant that a prompt answer saves has the
    /// text of the candidate verb and covers that candidate.
    /// </summary>
    /// <remarks>
    /// The test uses the production save path: <see cref="GrantBuilder"/> and
    /// <see cref="ToolApprovalActor.TryCreateEntries"/>. A candidate with no
    /// reusable phrase (an exact candidate, unknown command words, or an
    /// approval-exempt data command) saves no grant, so the rule skips it.
    /// The chat and the everywhere scope have no folder, so the phrase alone
    /// decides. <see cref="Saved_grant_covers_the_command_that_saved_it"/>
    /// proves the folder scope, which also depends on the path of each call.
    /// </remarks>
    [SlopwatchSuppress("SW001", "The catalog resolves POSIX paths with the Bash grammar.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The catalog resolves POSIX paths with the Bash grammar.")]
    public void Each_catalog_candidate_is_covered_by_the_grant_that_it_saves()
    {
        var checkedCandidates = 0;
        var failures = new List<string>();
        foreach (var invocation in ShellApprovalCases.All
                     .Select(static item => item.Invocation)
                     .DistinctBy(static item => (item.Command, item.Host)))
        {
            var environment = invocation.CreateEnvironment();
            var cwd = invocation.Host is ShellApprovalHost.Bash or ShellApprovalHost.Bash52
                ? "/work/project"
                : @"C:\work\project";
            var matcher = new ShellApprovalMatcher(environment);
            var analysis = matcher.AnalyzeInvocation(
                Shell,
                new Dictionary<string, object?>
                {
                    ["Command"] = invocation.Command,
                    ["WorkingDirectory"] = cwd,
                });

            var reusable = analysis.Candidates
                .Where(static candidate => candidate.VerbTokens is not null
                                           && candidate.Unresolved == ShellUnresolvedPart.None
                                           && !ApprovalPatternMatching.IsPureSideEffect(candidate))
                .ToArray();
            foreach (var kind in new[] { GrantScopeKind.Session, GrantScopeKind.Everywhere })
            {
                var grants = GrantBuilder.Build(reusable, kind, cwd, "/session/dir", repositoryCommonDirectory: null);
                Assert.True(ToolApprovalActor.TryCreateEntries(Shell, grants, out var persistent, out var session));
                var entries = kind == GrantScopeKind.Session ? session : persistent;
                for (var index = 0; index < grants.Count; index++)
                {
                    var candidate = grants[index].Candidate;
                    checkedCandidates++;
                    if (entries[index].Verb != candidate.Verb)
                    {
                        failures.Add($"[{invocation.Command}] shows '{candidate.Verb}' and saves '{entries[index].Verb}'");
                    }

                    if (!ApprovalPatternMatching.MatchesShellApproval(candidate, cwd, [entries[index]]))
                    {
                        failures.Add($"[{invocation.Command}] the {kind} grant '{entries[index].Verb}' does not cover its call");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        // The catalog must supply candidates, or the rule proves nothing.
        Assert.True(checkedCandidates > 500, $"Only {checkedCandidates} candidates were checked.");
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync()
        => ShellApprovalHarness.CreateAsync(
            "grant-identity",
            new ShellApprovalInvocation("true"),
            Approvals.None,
            fixture.ActorSystem,
            Ct);

    // The session actor builds and records the grants of a prompt answer.
    private static async Task<IReadOnlyList<ToolApprovalGrant>> AnswerAsync(
        ShellApprovalHarness harness,
        string command,
        GrantScopeKind scope)
    {
        var prompt = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, prompt.Outcome);
        var approval = Assert.IsType<ToolApprovalContext>(prompt.ApprovalContext);

        var grants = GrantBuilder.Build(
            approval.Candidates!,
            scope,
            approval.Cwd,
            harness.SessionDirectory,
            approval.RepositoryCommonDirectory);
        await harness.ApprovalService.RecordApprovalCandidatesAsync(
            InvocationSession,
            TrustAudience.Personal,
            Shell,
            grants,
            Ct);
        return grants;
    }

    private static async Task AssertAllowedByStoredGrantAsync(ShellApprovalHarness harness, string command)
    {
        var decision = await harness.EvaluateShellDecisionAsync(command, Ct);
        Assert.True(
            decision.Outcome == ToolAuthorizationOutcome.Allowed
            && decision.AllowReason == ToolAllowReason.StoredApproval,
            $"'{command}' was {decision.Outcome} ({decision.AllowReason}) {decision.DenyReason}; a stored grant should cover it.");
    }

    private static async Task AssertPromptsForAsync(ShellApprovalHarness harness, string command, string shownVerb)
    {
        var decision = await harness.EvaluateShellAsync(command, Ct);
        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        Assert.Contains(shownVerb, decision.Prompt!.CandidateVerbs);
    }
}
