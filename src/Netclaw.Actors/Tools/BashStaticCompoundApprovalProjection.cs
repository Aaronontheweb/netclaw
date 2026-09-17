// -----------------------------------------------------------------------
// <copyright file="BashStaticCompoundApprovalProjection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using Netclaw.Tools;
using ShellSyntaxTree;

namespace Netclaw.Actors.Tools;

internal sealed record ScopedShellApprovalSlice(
    ShellCommandAnalysis Analysis,
    ShellApprovalAnalysis Approval,
    string WorkingDirectory);

internal sealed record BashStaticCompoundApprovalProjection(
    IReadOnlyList<ApprovalCandidate> Candidates,
    IReadOnlyList<ScopedShellApprovalSlice> Slices)
{
    private const int MaximumDirectories = 32;
    private const int MaximumSlices = 128;
    private const int MaximumCandidates = 256;

    internal static bool TryCreate(
        ShellCommandAnalysis source,
        ShellCommandPolicy commandPolicy,
        ShellApprovalMatcher matcher,
        out BashStaticCompoundApprovalProjection? projection)
    {
        projection = null;
        if (source.Environment.Grammar != ShellGrammar.Bash
            || !source.IsResolved
            || source.RequiresExactTreeApproval
            || source.Commands.Count < 2
            || !source.Commands.Any(static command =>
                command.WorkingDirectoryEffect is ShellWorkingDirectoryEffect.ChangesOnSuccess
                {
                    Target: ShellValueDomain.Exact
                })
            || !ShellPathRules.TryNormalize(
                source.WorkingDirectory,
                ShellPathStyle.Posix,
                out var initialDirectory)
            || !TryGetList(source.Commands, out var list))
        {
            return false;
        }

        var sourceOccurrences = source.Commands.ToDictionary<CommandOccurrence, Clause>(
            static occurrence => occurrence.Clause,
            ReferenceEqualityComparer.Instance);
        var visited = new HashSet<Clause>(ReferenceEqualityComparer.Instance);
        var candidates = new List<ApprovalCandidate>();
        var slices = new List<ScopedShellApprovalSlice>();
        var success = new HashSet<string>(StringComparer.Ordinal);
        var failure = new HashSet<string>(StringComparer.Ordinal);

        for (var itemIndex = 0; itemIndex < list.Items.Count; itemIndex++)
        {
            var item = list.Items[itemIndex];
            if (!TryGetInputDirectories(
                    item.Operator,
                    itemIndex,
                    initialDirectory,
                    success,
                    failure,
                    out var input)
                || input.Count == 0
                || input.Count > MaximumDirectories
                || !TryGetSimpleCommands(item.Command, out var commands)
                || !ValidateOccurrences(
                    commands,
                    item.Command,
                    list,
                    itemIndex,
                    sourceOccurrences,
                    visited))
            {
                return false;
            }

            var itemSuccess = new HashSet<string>(StringComparer.Ordinal);
            var itemFailure = new HashSet<string>(StringComparer.Ordinal);
            foreach (var directory in input)
            {
                ShellWorkingDirectoryEffect? effect = null;
                foreach (var simple in commands)
                {
                    if (slices.Count >= MaximumSlices
                        || !TryAnalyzeSlice(
                            source,
                            simple,
                            directory,
                            commandPolicy,
                            matcher,
                            out var slice))
                    {
                        return false;
                    }

                    slices.Add(slice);
                    candidates.AddRange(slice.Approval.Candidates);
                    if (candidates.Count > MaximumCandidates)
                        return false;
                    var nextEffect = slice.Analysis.Commands[0].WorkingDirectoryEffect;
                    if (item.Command is PipelineSyntax
                        && nextEffect is not ShellWorkingDirectoryEffect.Unchanged)
                    {
                        return false;
                    }

                    effect = nextEffect;
                }

                if (item.Command is PipelineSyntax
                    || effect is ShellWorkingDirectoryEffect.Unchanged)
                {
                    itemSuccess.Add(directory);
                    itemFailure.Add(directory);
                }
                else if (effect is ShellWorkingDirectoryEffect.ChangesOnSuccess
                         { Target: ShellValueDomain.Exact exact }
                         && ShellPathRules.TryNormalize(
                             exact.Value,
                             ShellPathStyle.Posix,
                             out var target))
                {
                    itemSuccess.Add(target);
                    itemFailure.Add(directory);
                }
                else
                {
                    return false;
                }
            }

            var nextSuccess = item.Operator == CompoundOperator.OrIf
                ? Union(success, itemSuccess)
                : itemSuccess;
            var nextFailure = item.Operator == CompoundOperator.AndIf
                ? Union(failure, itemFailure)
                : itemFailure;
            if (Union(nextSuccess, nextFailure).Count > MaximumDirectories)
                return false;

            success = nextSuccess;
            failure = nextFailure;
        }

        if (visited.Count != source.Commands.Count || candidates.Count == 0)
            return false;

        projection = new BashStaticCompoundApprovalProjection(
            Array.AsReadOnly(candidates.ToArray()),
            Array.AsReadOnly(slices.ToArray()));
        return true;
    }

    private static bool TryGetList(
        IReadOnlyList<CommandOccurrence> occurrences,
        out CommandListSyntax list)
    {
        list = null!;
        var first = occurrences[0];
        if (first.Ancestry.Count < 2
            || first.Ancestry[0] is not
                { Ancestor: ShellBlockSyntax, Region: CommandAncestryRegion.Root, ChildIndex: 0 }
            || first.Ancestry[1] is not
                { Ancestor: CommandListSyntax topLevel, Region: CommandAncestryRegion.Statement }
            || topLevel.Items.Count < 2)
        {
            return false;
        }

        list = topLevel;
        return true;
    }

    private static bool TryGetInputDirectories(
        CompoundOperator operation,
        int itemIndex,
        string initialDirectory,
        HashSet<string> success,
        HashSet<string> failure,
        out HashSet<string> input)
    {
        input = itemIndex == 0
            ? new HashSet<string>([initialDirectory], StringComparer.Ordinal)
            : operation switch
            {
                CompoundOperator.Sequence => Union(success, failure),
                CompoundOperator.AndIf => new HashSet<string>(success, StringComparer.Ordinal),
                CompoundOperator.OrIf => new HashSet<string>(failure, StringComparer.Ordinal),
                _ => []
            };
        return itemIndex == 0
            ? operation == CompoundOperator.None
            : operation is CompoundOperator.Sequence or CompoundOperator.AndIf or CompoundOperator.OrIf;
    }

    private static HashSet<string> Union(
        IReadOnlySet<string> first,
        IReadOnlySet<string> second)
    {
        var union = new HashSet<string>(first, StringComparer.Ordinal);
        union.UnionWith(second);
        return union;
    }

    private static bool TryGetSimpleCommands(
        ShellSyntaxNode node,
        out IReadOnlyList<SimpleCommandSyntax> commands)
    {
        commands = node switch
        {
            SimpleCommandSyntax simple => [simple],
            PipelineSyntax pipeline when pipeline.Stages.Count >= 2
                && pipeline.Stages.All(static stage => stage is SimpleCommandSyntax) =>
                pipeline.Stages.Cast<SimpleCommandSyntax>().ToArray(),
            _ => []
        };
        return commands.Count > 0
            && commands.All(static simple =>
                simple.Substitutions.Count == 0
                && simple.ExecutionRegions.Count == 0
                && simple.SourceStart is >= 0
                && simple.SourceLength is > 0);
    }

    private static bool ValidateOccurrences(
        IReadOnlyList<SimpleCommandSyntax> commands,
        ShellSyntaxNode item,
        CommandListSyntax list,
        int itemIndex,
        IReadOnlyDictionary<Clause, CommandOccurrence> sourceOccurrences,
        HashSet<Clause> visited)
    {
        for (var stageIndex = 0; stageIndex < commands.Count; stageIndex++)
        {
            var simple = commands[stageIndex];
            if (!sourceOccurrences.TryGetValue(simple.Clause, out var occurrence)
                || !visited.Add(simple.Clause)
                || !occurrence.IsComplete
                || occurrence.Ancestry.Count != (item is PipelineSyntax ? 3 : 2)
                || occurrence.Ancestry[0] is not
                    { Ancestor: ShellBlockSyntax, Region: CommandAncestryRegion.Root, ChildIndex: 0 }
                || occurrence.Ancestry[1] is not
                    { Ancestor: var ancestor, Region: CommandAncestryRegion.Statement, ChildIndex: var childIndex }
                || !ReferenceEquals(ancestor, list)
                || childIndex != itemIndex)
            {
                return false;
            }

            if (item is PipelineSyntax pipeline)
            {
                if (occurrence.ImmediateRole != CommandOccurrenceRole.PipelineStage
                    || occurrence.Ancestry[2] is not
                        { Ancestor: var stageParent, Region: CommandAncestryRegion.PipelineStage, ChildIndex: var childStage }
                    || !ReferenceEquals(stageParent, pipeline)
                    || childStage != stageIndex)
                {
                    return false;
                }
            }
            else if (occurrence.ImmediateRole != CommandOccurrenceRole.Ordinary)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryAnalyzeSlice(
        ShellCommandAnalysis source,
        SimpleCommandSyntax simple,
        string directory,
        ShellCommandPolicy commandPolicy,
        ShellApprovalMatcher matcher,
        out ScopedShellApprovalSlice slice)
    {
        slice = null!;
        var start = simple.SourceStart!.Value;
        var length = simple.SourceLength!.Value;
        if (start > source.Source.Length || length > source.Source.Length - start)
            return false;

        var command = source.Source.Substring(start, length);
        var analysis = commandPolicy.Analyze(command, directory);
        if (!analysis.IsResolved
            || analysis.HasDynamicSyntax
            || analysis.RequiresExactTreeApproval
            || analysis.Commands.Count != 1
            || !analysis.Commands[0].IsComplete
            || !HasSameAuthoredElements(simple.Clause, analysis.Commands[0].Clause))
        {
            return false;
        }

        var approval = matcher.AnalyzeInvocation(
            new ToolName(ShellTool.ToolName),
            new Dictionary<string, object?>
            {
                ["Command"] = command,
                ["WorkingDirectory"] = directory
            },
            analysis);
        if (approval.IsMessy || approval.Candidates.Count == 0)
            return false;

        slice = new ScopedShellApprovalSlice(analysis, approval, directory);
        return true;
    }

    private static bool HasSameAuthoredElements(Clause first, Clause second)
        => first.Elements.Count == second.Elements.Count
           && first.Elements.Zip(second.Elements).All(static pair =>
               pair.First.Role == pair.Second.Role
               && string.Equals(pair.First.Raw, pair.Second.Raw, StringComparison.Ordinal));
}
