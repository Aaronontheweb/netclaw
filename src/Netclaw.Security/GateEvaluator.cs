// -----------------------------------------------------------------------
// <copyright file="GateEvaluator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using ShellSyntaxTree;

namespace Netclaw.Security;

/// <summary>
/// Three-layer approval gate evaluator. Composes hard-deny (Layer 1),
/// zone gate (Layer 2), and verb-pattern gate (Layer 3) into a single
/// <see cref="GateEvaluation"/> the workflow consumes.
/// </summary>
/// <remarks>
/// Pure function — no DI dependencies beyond the constructor inputs. Layer
/// 1 runs against the parsed clauses (using the structured hard-deny rule
/// set built from compiled defaults plus operator overrides). Layer 2
/// extracts every path each clause operates on from the AST and checks
/// each against the composed <see cref="TrustState"/>. Layer 3 evaluates
/// the verb chain: read-only verbs auto-pass when every clause path is
/// trusted; otherwise pattern-match or prompt.
///
/// Multi-path zone batching: when multiple clauses contribute untrusted
/// paths, a single <see cref="ZonePromptInfo"/> carries their dedup'd
/// union — the user clicks "Trust all listed (N)" once to grant the
/// entire batch atomically.
/// </remarks>
public sealed class GateEvaluator
{
    private readonly ShellCommandPolicy _hardDenyPolicy;
    private readonly IShellParser _parser;

    public GateEvaluator(ShellCommandPolicy hardDenyPolicy, IShellParser parser)
    {
        _hardDenyPolicy = hardDenyPolicy ?? throw new ArgumentNullException(nameof(hardDenyPolicy));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    /// <summary>
    /// Parses the command via <see cref="IShellParser"/> and evaluates the
    /// three layers against the supplied <see cref="TrustState"/>.
    /// </summary>
    public GateEvaluation Evaluate(string command, TrustAudience audience, TrustState trustState)
    {
        ArgumentNullException.ThrowIfNull(trustState);
        var parsed = _parser.Parse(command ?? string.Empty);
        return Evaluate(parsed, audience, trustState);
    }

    /// <summary>
    /// Evaluates an already-parsed command against the trust state. Useful
    /// for tests that inject custom ASTs or for callers that have a
    /// <see cref="ParsedCommand"/> cached from earlier in the pipeline.
    /// </summary>
    public GateEvaluation Evaluate(ParsedCommand parsed, TrustAudience audience, TrustState trustState)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(trustState);

        if (parsed.IsUnparseable)
            return EvaluateUnparseable(parsed, audience);

        return EvaluateParsed(parsed, audience, trustState);
    }

    private GateEvaluation EvaluateParsed(ParsedCommand parsed, TrustAudience audience, TrustState trustState)
    {
        var clauseResults = new List<ClauseGateResult>(parsed.Clauses.Count);
        var untrustedPathUnion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        VerbPromptInfo? firstVerbPrompt = null;
        string? firstHardDenyReason = null;
        string? firstHardDenyCategory = null;

        for (var i = 0; i < parsed.Clauses.Count; i++)
        {
            var clause = parsed.Clauses[i];
            var result = EvaluateClause(i, clause, trustState);
            clauseResults.Add(result);

            if (result.HardDenyReason is not null)
            {
                // First hard-deny wins overall; we keep evaluating
                // subsequent clauses so per-clause results reflect every
                // matching deny (useful for telemetry / auditing) but
                // the overall decision is fixed.
                firstHardDenyReason ??= result.HardDenyReason;
                firstHardDenyCategory ??= result.HardDenyCategory;
                continue;
            }

            if (result.ZoneDecision == ZoneGateDecision.Prompt)
            {
                foreach (var path in result.UntrustedPaths)
                    untrustedPathUnion.Add(path);
            }

            // Capture the first clause-level verb prompt; the workflow
            // surfaces this as THE verb prompt. Subsequent mutating
            // clauses with their own pattern proposals stay attached to
            // their ClauseGateResult records for audit.
            if (result.VerbDecision == VerbGateDecision.Prompt
                && firstVerbPrompt is null
                && result.VerbPatternProposal is not null)
            {
                firstVerbPrompt = new VerbPromptInfo
                {
                    VerbPattern = result.VerbPatternProposal,
                    CommandText = RenderClause(clause),
                    Audience = audience.ToWireValue()
                };
            }
        }

        if (firstHardDenyReason is not null)
        {
            return new GateEvaluation
            {
                OverallDecision = OverallGateDecision.HardDenied,
                ClauseResults = clauseResults,
                HardDenyReason = firstHardDenyReason,
                HardDenyCategory = firstHardDenyCategory
            };
        }

        var zonePrompt = untrustedPathUnion.Count > 0
            ? new ZonePromptInfo
            {
                UntrustedPaths = untrustedPathUnion.ToArray(),
                Audience = audience.ToWireValue()
            }
            : null;

        var needsPrompt = zonePrompt is not null || firstVerbPrompt is not null;

        return new GateEvaluation
        {
            OverallDecision = needsPrompt ? OverallGateDecision.NeedsPrompt : OverallGateDecision.Approved,
            ClauseResults = clauseResults,
            ZonePrompt = zonePrompt,
            VerbPrompt = firstVerbPrompt
        };
    }

    private ClauseGateResult EvaluateClause(int index, Clause clause, TrustState trustState)
    {
        // Layer 1: hard-deny against the rendered clause text. The
        // ShellCommandPolicy already handles structured + rawText
        // patterns via its existing engine (commit fdcbd387).
        var clauseText = RenderClause(clause);
        var hardDenyDecision = _hardDenyPolicy.Evaluate(clauseText);
        if (!hardDenyDecision.Allowed)
        {
            return new ClauseGateResult
            {
                ClauseIndex = index,
                ZoneDecision = ZoneGateDecision.Skipped,
                VerbDecision = VerbGateDecision.NotEvaluated,
                HardDenyReason = hardDenyDecision.DenyReason,
                HardDenyCategory = hardDenyDecision.DenyCategory
            };
        }

        // Layer 2: zone gate. Extract every path the clause operates on:
        // path args, redirect targets, and cd-in-compound attribution.
        var clausePaths = ExtractClausePaths(clause);
        var untrusted = clausePaths
            .Where(p => !trustState.IsPathInTrustedZone(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Layer 3: verb-pattern gate. Read-only verbs auto-pass ONLY when
        // every clause path is trusted (mixed-zone rule: if zone gate
        // prompted on this clause, the read-only auto-pass is conditional
        // on the user approving — the workflow handles that re-evaluation).
        var allPathsTrusted = clausePaths.Count == 0 || untrusted.Length == 0;
        var verbDecision = EvaluateVerbGate(clause, trustState, allPathsTrusted, out var verbPatternProposal);

        return new ClauseGateResult
        {
            ClauseIndex = index,
            ZoneDecision = untrusted.Length == 0 ? ZoneGateDecision.Pass : ZoneGateDecision.Prompt,
            UntrustedPaths = untrusted,
            VerbDecision = verbDecision,
            VerbPatternProposal = verbPatternProposal
        };
    }

    private static VerbGateDecision EvaluateVerbGate(
        Clause clause,
        TrustState trustState,
        bool allPathsTrusted,
        out string? verbPatternProposal)
    {
        verbPatternProposal = null;
        _ = allPathsTrusted; // intentionally unused — see rationale below.

        // Empty verb (clause is just a redirect, or a tokenizer artifact):
        // no verb to gate. Layer 2 still applied to the redirect target.
        if (clause.Verb.Tokens.Count == 0)
            return VerbGateDecision.Pass;

        // Read-only verbs unconditionally pass the verb gate. The zone
        // gate handles the geography concern (it will prompt for any
        // untrusted paths). The verb gate is purely about whether the
        // action shape is dangerous — read-only actions are by
        // definition not dangerous regardless of where they run. After
        // zone approval, all clause paths are in trusted scope and the
        // call proceeds. The user sees exactly one prompt (the zone
        // prompt) for read-only verbs on untrusted paths — never two.
        if (trustState.IsReadOnlyVerb(clause.Verb))
            return VerbGateDecision.Pass;

        // Pattern match: if the clause matches a persisted or session
        // verb pattern, silent pass.
        if (trustState.MatchesVerbPattern(clause.Verb, clause.Args))
            return VerbGateDecision.Pass;

        // No auto-pass available — propose a verb-pattern for the prompt.
        // Derivation rule per the locked design: <verb-chain> *.
        verbPatternProposal = clause.Verb.Joined + " *";
        return VerbGateDecision.Prompt;
    }

    private static IReadOnlyList<string> ExtractClausePaths(Clause clause)
    {
        var paths = new List<string>();

        foreach (var arg in clause.Args)
        {
            if (!arg.IsPath)
                continue;

            // Skip dynamic-content tokens — we can't resolve them safely,
            // so we treat the clause as path-arg-less for zone-gate
            // purposes. The verb gate still applies.
            if (arg.Kind == ArgKind.DynamicSkip)
                continue;

            var path = arg.Resolved ?? arg.Raw;
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        foreach (var redirect in clause.Redirects)
        {
            if (redirect.IsDynamicSkip)
                continue;

            if (!string.IsNullOrWhiteSpace(redirect.Target))
                paths.Add(redirect.Target);
        }

        return paths;
    }

    private GateEvaluation EvaluateUnparseable(ParsedCommand parsed, TrustAudience audience)
    {
        // Per the spec's "Parser anomaly safe-fail" requirement: hard-deny
        // still consults against the raw source; zone gate prompts as if
        // the entire raw command is one untrusted path; verb gate offers
        // Once / Deny only (the IsUnparseable flag on GateEvaluation
        // signals workflow to constrain prompt options).
        var rawText = parsed.Source ?? string.Empty;
        var hardDenyDecision = _hardDenyPolicy.Evaluate(rawText);
        if (!hardDenyDecision.Allowed)
        {
            return new GateEvaluation
            {
                OverallDecision = OverallGateDecision.HardDenied,
                ClauseResults = [],
                HardDenyReason = hardDenyDecision.DenyReason,
                HardDenyCategory = hardDenyDecision.DenyCategory,
                IsUnparseable = true
            };
        }

        return new GateEvaluation
        {
            OverallDecision = OverallGateDecision.NeedsPrompt,
            ClauseResults = [],
            IsUnparseable = true,
            ZonePrompt = new ZonePromptInfo
            {
                UntrustedPaths = [string.IsNullOrEmpty(rawText) ? "<unparseable>" : rawText],
                Audience = audience.ToWireValue()
            },
            VerbPrompt = new VerbPromptInfo
            {
                VerbPattern = string.Empty,
                CommandText = rawText,
                Audience = audience.ToWireValue()
            }
        };
    }

    private static string RenderClause(Clause clause)
    {
        // Approximate the original clause text by joining the verb chain
        // with non-attribution args and redirect operators. Doesn't try to
        // re-quote — used only by hard-deny rawText matching, which is
        // already substring-based, and by VerbPromptInfo.CommandText for
        // user display. Quote-sensitive matching belongs in structured
        // hard-deny rules, not rawText.
        var parts = new List<string>();
        if (clause.Verb.Tokens.Count > 0)
            parts.AddRange(clause.Verb.Tokens);

        foreach (var arg in clause.Args)
        {
            if (arg.IsCwdAttribution)
                continue;
            if (!string.IsNullOrEmpty(arg.Raw))
                parts.Add(arg.Raw);
        }

        foreach (var redirect in clause.Redirects)
        {
            parts.Add(RedirectOperator(redirect.Direction));
            if (!string.IsNullOrEmpty(redirect.Target))
                parts.Add(redirect.Target);
        }

        return string.Join(' ', parts);
    }

    private static string RedirectOperator(RedirectDirection direction)
        => direction switch
        {
            RedirectDirection.In => "<",
            RedirectDirection.Out => ">",
            RedirectDirection.Append => ">>",
            RedirectDirection.ErrOut => "2>",
            RedirectDirection.ErrAppend => "2>>",
            _ => ">"
        };
}
