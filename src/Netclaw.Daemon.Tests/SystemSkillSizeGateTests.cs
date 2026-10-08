// -----------------------------------------------------------------------
// <copyright file="SystemSkillSizeGateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests;

/// <summary>
/// The daemon bounds each tool result to the inline budget
/// (<see cref="SessionTuning.MaxInlineToolResultChars"/>). A longer result reaches
/// the model as its first half and its last half. The model does not read the
/// middle of an oversized skill file unless it reads more. This gate fails when
/// the text that <c>skill_load</c> or <c>skill_read_resource</c> returns for a
/// system skill file is longer than that budget.
/// </summary>
public sealed class SystemSkillSizeGateTests : IDisposable
{
    // skill_read_resource puts the absolute path of the resource on the first
    // line of its result. The length of that line depends on the install folder,
    // so the gate removes the real line and reserves this length for it.
    private const int ResourcePathLineReserve = 200;

    /// <summary>
    /// The files that are over the budget today. Each entry has the largest
    /// result length that the gate accepts for that file, so the file cannot
    /// grow, and the follow-up work that removes the entry. Do not add an entry
    /// for a new file: make the file shorter, or move detail into a reference.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, SizeExemption> Exemptions =
        new Dictionary<string, SizeExemption>(StringComparer.Ordinal)
        {
            ["netclaw-operations/SKILL.md"] = new(
                57_300,
                "Split netclaw-operations into an index and reference files. The plan is in the pull request that added this gate."),
            ["netclaw-operations/references/scheduling.md"] = new(
                21_600,
                "Split the scheduling reference (reminders, proactive channel messages, approvals, background jobs) with the netclaw-operations split."),
            ["netclaw-operations/references/diagnostics.md"] = new(
                13_200,
                "Split the diagnostics reference (diagnostics, kill switches, self-maintenance) with the netclaw-operations split."),
            ["skill-authoring/SKILL.md"] = new(
                12_400,
                "Move the authoring detail of skill-authoring into a reference file."),
        };

    private readonly DisposableTempDir _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Each_system_skill_result_fits_the_inline_budget_or_has_an_exemption()
    {
        var budget = new SessionTuning().MaxInlineToolResultChars;
        var results = await MeasureSystemSkillResultsAsync();
        var failures = new List<string>();

        foreach (var (file, length) in results)
        {
            if (Exemptions.TryGetValue(file, out var exemption))
            {
                if (length <= budget)
                {
                    failures.Add(
                        $"{file}: the result has {length} characters and fits the budget of {budget}. Remove its exemption.");
                }
                else if (length > exemption.MaxResultChars)
                {
                    failures.Add(
                        $"{file}: the result has {length} characters. Its exemption accepts {exemption.MaxResultChars}. " +
                        $"An exempt file must not grow. Follow-up: {exemption.FollowUp}");
                }

                continue;
            }

            if (length > budget)
            {
                failures.Add(
                    $"{file}: the result has {length} characters. The inline budget is {budget}, so the model " +
                    $"does not read the middle {length - budget} characters. Make the file shorter, or move detail " +
                    "into a reference file and add one line to SKILL.md that says when to read it.");
            }
        }

        foreach (var file in Exemptions.Keys.Where(file => !results.ContainsKey(file)))
            failures.Add($"{file}: the exemption names a file that does not exist. Remove the exemption.");

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task The_gate_measures_each_system_skill_and_each_reference()
    {
        var results = await MeasureSystemSkillResultsAsync();
        var sourceDirectory = FindSourceDirectory();
        var sourceFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceDirectory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // A file that the gate does not measure is a file that can grow.
        Assert.Equal(sourceFiles, results.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Restores the embedded system skills, scans them, and calls the two skill
    /// tools for each file. The key is "skill/relative-path". The value is the
    /// length of the text that the model gets before the daemon bounds it.
    /// </summary>
    private async Task<Dictionary<string, int>> MeasureSystemSkillResultsAsync()
    {
        var paths = new NetclawPaths(Path.Combine(_directory.Path, Guid.NewGuid().ToString("N")));
        paths.EnsureDirectoriesExist();
        EmbeddedSystemSkillRestorer.Restore(paths);

        var registry = new SkillRegistry();
        var refresher = new SkillInventoryRefresher(
            paths,
            new SkillFeedsConfig(),
            [],
            registry,
            new SkillIndexPublisher(registry, new SkillIndexContextLayer(), static (_, _) => true));
        refresher.Refresh();

        var scanner = new NoOpSkillContentScanner();
        var loadTool = new SkillLoadTool(registry, scanner, new UnavailablePromptLoader());
        var resourceTool = new SkillReadResourceTool(registry, scanner);
        var context = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
        });
        var cancellationToken = TestContext.Current.CancellationToken;
        var results = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var skillDirectory in Directory.GetDirectories(paths.SystemSkillsDirectory))
        {
            var skillName = Path.GetFileName(skillDirectory);
            var skill = registry.GetByName(skillName);
            Assert.NotNull(skill);

            var loaded = await loadTool.ExecuteAsync(ToolInput.Create("Name", skillName), context, cancellationToken);
            Assert.StartsWith("## ", loaded, StringComparison.Ordinal);
            results[$"{skillName}/SKILL.md"] = loaded.Length;

            foreach (var resourcePath in skill.ResourcePaths ?? [])
            {
                var resource = await resourceTool.ExecuteAsync(
                    ToolInput.Create("SkillName", skillName, "ResourcePath", resourcePath),
                    context,
                    cancellationToken);
                Assert.StartsWith("path: ", resource, StringComparison.Ordinal);
                var pathLineLength = resource.IndexOf('\n', StringComparison.Ordinal) + 1;
                results[$"{skillName}/{resourcePath}"] = resource.Length - pathLineLength + ResourcePathLineReserve;
            }
        }

        return results;
    }

    private static string FindSourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "feeds", "skills", ".system", "files");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("The system skill source folder was not found above the test folder.");
    }

    private sealed record SizeExemption(int MaxResultChars, string FollowUp);

    private sealed class UnavailablePromptLoader : IMcpPromptSkillLoader
    {
        public ValueTask<McpPromptSkillLoadResult> LoadAsync(
            McpPromptSkillSource source,
            IReadOnlyDictionary<string, string>? arguments,
            ToolInvocationContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(McpPromptSkillLoadResult.Failed("Prompt loading is unavailable in this test."));
    }
}
