// -----------------------------------------------------------------------
// <copyright file="SlackApprovalBlockBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Slack;
using Netclaw.Tools;
using SlackNet.Blocks;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class SlackApprovalBlockBuilderTests
{
    private static IReadOnlyList<ToolInteractionOption> FullButtonRow() =>
    [
        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveSession, ApprovalOptionKeys.ApproveSessionLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveAlways, ApprovalOptionKeys.ApproveAlwaysLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhere, ApprovalOptionKeys.ApproveEverywhereLabel),
        new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
    ];

    private static IReadOnlyList<ToolInteractionOption> MessyRow() =>
    [
        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
        new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
    ];

    private static ToolInteractionRequest Request(
        string command,
        IReadOnlyList<string> verbs,
        string? cwd,
        IReadOnlyList<ToolInteractionOption> options,
        bool isMessy = false)
        => new()
        {
            SessionId = new SessionId("signalr/test"),
            Kind = "approval",
            CallId = "call-1",
            ToolName = "shell_execute",
            DisplayText = command,
            RequesterSenderId = "device-1",
            Patterns = verbs,
            CandidateVerbs = verbs,
            Cwd = cwd,
            IsMessy = isMessy,
            Options = options
        };

    [Fact]
    public void Single_verb_collapses_into_header_line()
    {
        var request = Request("git status", ["git status"], "/home/user/repos/foo", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildApprovalText(request);

        Assert.Contains("Approve git status in /home/user/repos/foo?", text);
        Assert.DoesNotContain("• `git status`", text); // No redundant bullet for single-verb
    }

    [Fact]
    public void Multi_verb_uses_generic_header_with_bulleted_verbs()
    {
        var request = Request(
            "git fetch && git rebase && git status",
            ["git fetch", "git rebase", "git status"],
            "/home/user/repos/foo",
            FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildApprovalText(request);

        Assert.Contains("Approve in /home/user/repos/foo?", text);
        Assert.Contains("• `git fetch`", text);
        Assert.Contains("• `git rebase`", text);
        Assert.Contains("• `git status`", text);
    }

    [Fact]
    public void Messy_command_emits_complex_command_hint()
    {
        var request = Request(
            "for f in *.log; do grep ERROR \"$f\"; done",
            verbs: [],
            cwd: "/home/user/repos/foo",
            options: MessyRow(),
            isMessy: true);

        var text = SlackApprovalBlockBuilder.BuildApprovalText(request);

        Assert.Contains("complex command", text);
        Assert.Contains("only one-shot approval available", text);
    }

    [Fact]
    public void Approval_blocks_render_five_buttons_with_danger_styling_on_danger_options()
    {
        var request = Request("git status", ["git status"], "/home/user/repos/foo", FullButtonRow());

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var actions = blocks.OfType<ActionsBlock>().Single();
        var buttons = actions.Elements.OfType<Button>().ToList();

        Assert.Equal(5, buttons.Count);

        var byKey = buttons.ToDictionary(b => b.ActionId.Split('_').Last(), b => b);
        Assert.Equal(ButtonStyle.Primary, byKey["once"].Style);
        Assert.Equal(ButtonStyle.Default, byKey["session"].Style);
        Assert.Equal(ButtonStyle.Default, byKey["always"].Style);
        Assert.Equal(ButtonStyle.Danger, byKey["everywhere"].Style);
        Assert.Equal(ButtonStyle.Danger, byKey["deny"].Style);
    }

    [Fact]
    public void Approval_blocks_omit_legacy_directory_roots_section()
    {
        var request = Request("grep error /var/log/syslog", ["grep error /var/log/syslog"], "/var/log", FullButtonRow());

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var sections = blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(sections, t => t.Contains("Directory Roots", StringComparison.Ordinal));
        Assert.DoesNotContain(sections, t => t.Contains("*Patterns*", StringComparison.Ordinal));
    }

    // ── Resolution message single-line format ──

    [Fact]
    public void Resolved_text_for_always_here_uses_Saved_verbs_in_dir()
    {
        var request = Request("git pull && git rebase", ["git pull", "git rebase"], "/home/user/repos/foo", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(request, ApprovalOptionKeys.ApproveAlways, "U123");

        Assert.Contains("Saved: git pull, git rebase in /home/user/repos/foo", text);
    }

    [Fact]
    public void Resolved_text_for_always_anywhere_uses_Saved_verbs_anywhere()
    {
        var request = Request("freshdesk --since=24h", ["freshdesk"], "/home/user/.netclaw/sessions/abc", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(request, ApprovalOptionKeys.ApproveEverywhere, "U123");

        Assert.Contains("Saved: freshdesk anywhere", text);
    }

    [Fact]
    public void Resolved_text_for_this_chat_uses_Saved_for_this_chat()
    {
        var request = Request("jsonlint config.json", ["jsonlint config.json"], "/home/user/repos/foo", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(request, ApprovalOptionKeys.ApproveSession, "U123");

        Assert.Contains("Saved for this chat: jsonlint config.json in /home/user/repos/foo", text);
    }

    [Fact]
    public void Resolved_text_for_once_uses_Approved_no_save()
    {
        var request = Request("docker build .", ["docker build"], "/home/user/repos/foo", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(request, ApprovalOptionKeys.ApproveOnce, "U123");

        Assert.Contains("Approved (no save)", text);
    }

    [Fact]
    public void Resolved_text_for_deny_uses_Denied()
    {
        var request = Request("rm -rf /", ["rm"], "/home/user/repos/foo", FullButtonRow());

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(request, ApprovalOptionKeys.Deny, "U123");

        Assert.Contains("Denied", text);
    }

    // ── Trust-zones zone-gate prompts (Kind = "approval_zone") ──

    private static IReadOnlyList<ToolInteractionOption> ZoneButtonRow() =>
    [
        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveSession, ApprovalOptionKeys.ApproveSessionLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveAlways, ApprovalOptionKeys.ApproveAlwaysLabel),
        new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
    ];

    private static ToolInteractionRequest ZoneRequest(
        string command,
        IReadOnlyList<string> untrustedPaths,
        IReadOnlyList<ToolInteractionOption>? options = null)
        => new()
        {
            SessionId = new SessionId("signalr/test"),
            Kind = "approval_zone",
            CallId = "call-zone-1",
            ToolName = "shell_execute",
            DisplayText = command,
            RequesterSenderId = "device-1",
            Patterns = untrustedPaths,
            CandidateVerbs = [],
            Cwd = null,
            IsMessy = false,
            Options = options ?? ZoneButtonRow()
        };

    [Fact]
    public void Zone_prompt_single_path_renders_path_in_button_and_header()
    {
        var request = ZoneRequest("cat /etc/nginx/nginx.conf", ["/etc/nginx"]);

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var actions = blocks.OfType<ActionsBlock>().Single();
        var buttons = actions.Elements.OfType<Button>().ToList();
        var sections = blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? string.Empty)
            .ToList();

        Assert.Contains(sections, t => t.Contains("Trust this path?", StringComparison.Ordinal));
        Assert.Contains(sections, t => t.Contains("`/etc/nginx`", StringComparison.Ordinal));

        var byKey = buttons.ToDictionary(b => b.ActionId.Split('_').Last(), b => b);
        Assert.Equal("Trust /etc/nginx", byKey["always"].Text.Text);
    }

    [Fact]
    public void Zone_prompt_multi_path_uses_count_in_button_and_header()
    {
        var request = ZoneRequest(
            "cp /etc/nginx/conf /var/log/backup",
            ["/etc/nginx", "/var/log"]);

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var actions = blocks.OfType<ActionsBlock>().Single();
        var buttons = actions.Elements.OfType<Button>().ToList();
        var sections = blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? string.Empty)
            .ToList();

        Assert.Contains(sections, t => t.Contains("Trust these 2 paths?", StringComparison.Ordinal));

        var byKey = buttons.ToDictionary(b => b.ActionId.Split('_').Last(), b => b);
        Assert.Equal("Trust all 2 listed", byKey["always"].Text.Text);
    }

    [Fact]
    public void Zone_prompt_truncates_long_path_in_button_label_but_keeps_it_in_body()
    {
        // Long-enough path that "Trust <path>" would blow past Slack's
        // 76-char button cap. Button label gets ellipsized; body keeps
        // the full path so the operator can still see what they're
        // trusting before they click.
        var longPath = "/home/user/repos/petabridge/very-long-organization-name/some-deeply-nested-project/build/output";
        var request = ZoneRequest($"cat {longPath}/log.txt", [longPath]);

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var actions = blocks.OfType<ActionsBlock>().Single();
        var buttons = actions.Elements.OfType<Button>().ToList();
        var sections = blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? string.Empty)
            .ToList();

        var byKey = buttons.ToDictionary(b => b.ActionId.Split('_').Last(), b => b);
        var trustLabel = byKey["always"].Text.Text;
        Assert.True(trustLabel.Length <= ApprovalOptionKeys.MaxLabelLength,
            $"Trust button label '{trustLabel}' exceeds {ApprovalOptionKeys.MaxLabelLength}-char cap");
        Assert.EndsWith("…", trustLabel);

        Assert.Contains(sections, t => t.Contains(longPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Zone_prompt_renders_four_buttons_no_everywhere()
    {
        // Zone prompts don't expose ApproveEverywhere — a zone is always
        // a concrete directory; there's no "anywhere" form to persist.
        var request = ZoneRequest("cat /etc/nginx/conf", ["/etc/nginx"]);

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var actions = blocks.OfType<ActionsBlock>().Single();
        var keys = actions.Elements.OfType<Button>()
            .Select(b => b.ActionId.Split('_').Last())
            .ToList();

        Assert.Equal(4, keys.Count);
        Assert.Contains("once", keys);
        Assert.Contains("session", keys);
        Assert.Contains("always", keys);
        Assert.Contains("deny", keys);
        Assert.DoesNotContain("everywhere", keys);
    }

    [Fact]
    public void Zone_prompt_resolution_uses_Saved_zone_for_Always()
    {
        var request = ZoneRequest("cat /etc/nginx/conf", ["/etc/nginx"]);

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(
            request, ApprovalOptionKeys.ApproveAlways, "U123");

        Assert.Contains("Saved zone: /etc/nginx", text);
    }

    [Fact]
    public void Zone_prompt_resolution_uses_Saved_zone_for_this_chat_for_Session()
    {
        var request = ZoneRequest("cat /etc/nginx/conf", ["/etc/nginx", "/var/log"]);

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(
            request, ApprovalOptionKeys.ApproveSession, "U123");

        Assert.Contains("Saved zone for this chat: /etc/nginx, /var/log", text);
    }

    // ── Trust-zones verb-gate prompts (Kind = "approval_verb") ──

    private static IReadOnlyList<ToolInteractionOption> VerbButtonRow() =>
    [
        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveSession, ApprovalOptionKeys.ApproveSessionLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveAlways, ApprovalOptionKeys.ApproveAlwaysLabel),
        new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhere, ApprovalOptionKeys.ApproveEverywhereLabel),
        new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
    ];

    private static ToolInteractionRequest VerbRequest(
        string command,
        string verbPattern,
        IReadOnlyList<ToolInteractionOption>? options = null)
        => new()
        {
            SessionId = new SessionId("signalr/test"),
            Kind = "approval_verb",
            CallId = "call-verb-1",
            ToolName = "shell_execute",
            DisplayText = command,
            RequesterSenderId = "device-1",
            Patterns = [verbPattern],
            CandidateVerbs = [verbPattern],
            Cwd = null,
            IsMessy = false,
            Options = options ?? VerbButtonRow()
        };

    [Fact]
    public void Verb_prompt_renders_pattern_in_button_and_body()
    {
        var request = VerbRequest("git push origin main", "git push origin main *");

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var actions = blocks.OfType<ActionsBlock>().Single();
        var buttons = actions.Elements.OfType<Button>().ToList();
        var sections = blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? string.Empty)
            .ToList();

        Assert.Contains(sections, t => t.Contains("Approve this verb pattern?", StringComparison.Ordinal));
        Assert.Contains(sections, t => t.Contains("`git push origin main *`", StringComparison.Ordinal));

        var byKey = buttons.ToDictionary(b => b.ActionId.Split('_').Last(), b => b);
        Assert.Equal("Always git push origin main *", byKey["always"].Text.Text);
        Assert.Equal("Always anywhere", byKey["everywhere"].Text.Text);
    }

    [Fact]
    public void Verb_prompt_renders_five_buttons_including_everywhere()
    {
        var request = VerbRequest("git push origin main", "git push origin main *");

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var keys = blocks.OfType<ActionsBlock>().Single().Elements.OfType<Button>()
            .Select(b => b.ActionId.Split('_').Last())
            .ToList();

        Assert.Equal(5, keys.Count);
        Assert.Contains("everywhere", keys);  // verb prompts DO expose Everywhere
    }

    [Fact]
    public void Verb_prompt_truncates_long_pattern_in_button_label()
    {
        var longPattern = "kubectl apply -f some-very-long-manifest-name-with-an-overly-descriptive-suffix.yaml *";
        var request = VerbRequest("kubectl apply ...", longPattern);

        var blocks = SlackApprovalBlockBuilder.BuildApprovalBlocks(request);
        var actions = blocks.OfType<ActionsBlock>().Single();
        var byKey = actions.Elements.OfType<Button>()
            .ToDictionary(b => b.ActionId.Split('_').Last(), b => b);

        var alwaysLabel = byKey["always"].Text.Text;
        Assert.True(alwaysLabel.Length <= ApprovalOptionKeys.MaxLabelLength,
            $"Always button label '{alwaysLabel}' exceeds {ApprovalOptionKeys.MaxLabelLength}-char cap");
        Assert.EndsWith("…", alwaysLabel);
    }

    [Fact]
    public void Verb_prompt_resolution_uses_Saved_verb_for_Always()
    {
        var request = VerbRequest("git push origin main", "git push origin main *");

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(
            request, ApprovalOptionKeys.ApproveAlways, "U123");

        Assert.Contains("Saved verb: git push origin main *", text);
    }

    [Fact]
    public void Verb_prompt_resolution_uses_Saved_verb_anywhere_for_Everywhere()
    {
        var request = VerbRequest("freshdesk ticket list --status open", "freshdesk ticket list *");

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(
            request, ApprovalOptionKeys.ApproveEverywhere, "U123");

        Assert.Contains("Saved verb (anywhere): freshdesk ticket list *", text);
    }

    [Fact]
    public void Verb_prompt_resolution_uses_Saved_verb_for_this_chat_for_Session()
    {
        var request = VerbRequest("rm /tmp/cache", "rm *");

        var text = SlackApprovalBlockBuilder.BuildResolvedApprovalText(
            request, ApprovalOptionKeys.ApproveSession, "U123");

        Assert.Contains("Saved verb for this chat: rm *", text);
    }
}
