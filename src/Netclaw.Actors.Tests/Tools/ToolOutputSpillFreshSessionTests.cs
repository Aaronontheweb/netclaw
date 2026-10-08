// -----------------------------------------------------------------------
// <copyright file="ToolOutputSpillFreshSessionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// A session gets its workspace folder on first use. Before the fix, only a
/// shell launch created it, so a session with no earlier shell call got an
/// oversized result with no call id and no retained text. These tests run the
/// real dispatcher on a session folder that does not exist.
/// </summary>
public sealed class ToolOutputSpillFreshSessionTests : IDisposable
{
    private const string HiddenRule = "HIDDEN-MIDDLE-RULE-7c41";
    private const string OversizedProbe = "oversized_probe";

    private readonly DisposableTempDir _temp = new();
    private readonly NetclawPaths _paths;
    private readonly string _sessionDirectory;
    private readonly DispatchingToolExecutor _executor;
    private readonly int _budget = new SessionTuning().MaxInlineToolResultChars;

    public ToolOutputSpillFreshSessionTests()
    {
        _paths = new NetclawPaths(Path.Combine(_temp.Path, "home"));
        _paths.EnsureDirectoriesExist();
        // The envelope exists (the session log is in it). The workspace does not.
        var envelope = Path.Combine(_temp.Path, "sessions", "fresh-session");
        Directory.CreateDirectory(Path.Combine(envelope, "logs"));
        _sessionDirectory = Path.Combine(envelope, "workspace");

        var skillDirectory = Path.Combine(_paths.SkillsDirectory, "oversized-skill");
        Directory.CreateDirectory(Path.Combine(skillDirectory, "references"));
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            "---\nname: oversized-skill\ndescription: A skill that is longer than the inline budget.\n---\n\n"
            + "# Oversized Skill\n\n" + OversizedText());
        File.WriteAllText(Path.Combine(skillDirectory, "references", "long.md"), OversizedText());

        var skills = new SkillRegistry();
        var scan = SkillScanner.Scan(_paths.SkillsDirectory);
        skills.ReplaceAll(scan.AcceptedSkills, scan.Issues);

        var scanner = new NoOpSkillContentScanner();
        var registry = new ToolRegistry();
        registry.RegisterCore(new SkillLoadTool(skills, scanner, new UnavailablePromptLoader()));
        registry.RegisterCore(new SkillReadResourceTool(skills, scanner));
        registry.RegisterCore(new ToolOutputReadTool());
        registry.Register(new FakeNetclawTool(OversizedProbe, OversizedText()));

        _executor = new DispatchingToolExecutor(registry, TestToolAccessPolicy.Create(new ToolConfig()));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Skill_load_in_a_session_with_no_shell_call_gives_a_call_id_for_the_hidden_middle()
    {
        var result = await ExecuteAsync("call-skill-load", "skill_load", ToolInput.Create("Name", "oversized-skill"));

        await AssertHiddenMiddleIsReadableAsync("call-skill-load", result);
    }

    [Fact]
    public async Task Skill_read_resource_in_a_session_with_no_shell_call_gives_a_call_id_for_the_hidden_middle()
    {
        var result = await ExecuteAsync(
            "call-skill-resource",
            "skill_read_resource",
            ToolInput.Create("SkillName", "oversized-skill", "ResourcePath", "references/long.md"));

        await AssertHiddenMiddleIsReadableAsync("call-skill-resource", result);
    }

    [Fact]
    public async Task Another_tool_in_a_session_with_no_shell_call_gives_a_call_id_for_the_hidden_middle()
    {
        var result = await ExecuteAsync("call-probe", OversizedProbe, ToolInput.Empty());

        await AssertHiddenMiddleIsReadableAsync("call-probe", result);
    }

    private async Task AssertHiddenMiddleIsReadableAsync(string callId, string result)
    {
        // The inline window has the head and the tail, not the middle.
        Assert.Contains("[output truncated to", result, StringComparison.Ordinal);
        Assert.DoesNotContain(HiddenRule, result, StringComparison.Ordinal);
        Assert.Contains($"tool_output_read using CallId='{callId}'", result, StringComparison.Ordinal);
        Assert.DoesNotContain(_sessionDirectory, result, StringComparison.Ordinal);
        Assert.True(Directory.Exists(_sessionDirectory));

        var found = false;
        for (var start = 0; start < 60_000 && !found; start += ToolOutputReadTool.MaximumLimit - 500)
        {
            var window = await ExecuteAsync(
                $"{callId}-read-{start}",
                ToolOutputReadTool.ToolName,
                ToolInput.Create("CallId", callId, "Start", start, "Limit", ToolOutputReadTool.MaximumLimit));
            found = window.Contains(HiddenRule, StringComparison.Ordinal);
            if (window.Contains("complete=true", StringComparison.Ordinal))
                break;
        }

        Assert.True(found, "tool_output_read did not return the text that the inline window removed.");
    }

    private Task<string> ExecuteAsync(string callId, string toolName, IDictionary<string, object?> arguments)
    {
        var callArguments = new Dictionary<string, object?>(arguments, StringComparer.Ordinal)
        {
            ["_rationale"] = "Verify the spill of a session with no workspace folder."
        };
        var context = TestToolExecutionContext.CreateBound(
            "signalr/fresh-session",
            _sessionDirectory,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal });
        return _executor.ExecuteAsync(
            new FunctionCallContent(callId, toolName, callArguments),
            context,
            TestContext.Current.CancellationToken);
    }

    // Three times the budget, with one marker in the middle third.
    private string OversizedText()
    {
        var filler = string.Concat(Enumerable.Repeat("This line is ordinary skill text.\n", _budget / 34 + 1));
        return filler + $"\n{HiddenRule}\n\n" + filler + filler;
    }

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
