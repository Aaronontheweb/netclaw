// -----------------------------------------------------------------------
// <copyright file="SlackApprovalBlockBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using SlackNet.Blocks;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Renders the three approval prompt shapes Slack supports:
/// v2 single-prompt (<c>Kind = "approval"</c>), trust-zones zone-gate
/// (<c>Kind = "approval_zone"</c>), and trust-zones verb-gate
/// (<c>Kind = "approval_verb"</c>). The block builder is the only
/// place that varies output by <see cref="ToolInteractionRequest.Kind"/>;
/// the upstream actor and pipeline don't need to know which channel
/// is rendering.
/// </summary>
internal static class SlackApprovalBlockBuilder
{
    public const string ApprovalActionId = "tool_approval";

    private const string ComplexCommandHint = "_complex command — only one-shot approval available_";

    public static string BuildApprovalText(ToolInteractionRequest request) => request.Kind switch
    {
        "approval_zone" => BuildZoneApprovalText(request),
        "approval_verb" => BuildVerbApprovalText(request),
        _ => BuildLegacyApprovalText(request)
    };

    public static IReadOnlyList<Block> BuildApprovalBlocks(ToolInteractionRequest request) => request.Kind switch
    {
        "approval_zone" => BuildZoneApprovalBlocks(request),
        "approval_verb" => BuildVerbApprovalBlocks(request),
        _ => BuildLegacyApprovalBlocks(request)
    };

    public static string BuildResolvedApprovalText(
        ToolInteractionRequest request,
        string selectedKey,
        string senderId)
    {
        var statusPrefix = selectedKey == ApprovalOptionKeys.Deny
            ? ":no_entry:"
            : ":white_check_mark:";

        return string.Join("\n", new[]
        {
            $"{statusPrefix} *Tool approval resolved* by <@{EscapeMarkdown(senderId)}>",
            $"> `{request.ToolName}`: `{request.DisplayText}`",
            BuildResolutionLine(request, selectedKey)
        });
    }

    public static IReadOnlyList<Block> BuildResolvedApprovalBlocks(
        ToolInteractionRequest request,
        string selectedKey,
        string senderId)
    {
        var statusPrefix = selectedKey == ApprovalOptionKeys.Deny
            ? ":no_entry:"
            : ":white_check_mark:";
        var resolutionLine = BuildResolutionLine(request, selectedKey);

        var blocks = new List<Block>
        {
            new SectionBlock
            {
                Text = new Markdown($"{statusPrefix} *Tool approval resolved* by <@{EscapeMarkdown(senderId)}>")
            },
            new SectionBlock
            {
                Text = new Markdown(
                    $"*Tool:* `{EscapeMarkdown(request.ToolName)}`\n"
                    + $"*Request:* `{EscapeMarkdown(request.DisplayText)}`\n"
                    + $"*{EscapeMarkdown(resolutionLine)}*"),
                Expand = true
            }
        };

        if (request.HasAdoptedContext)
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(BuildAdoptedContextMarkdown(request))
            });
        }

        return blocks;
    }

    // -------------------------------------------------------------------
    // Legacy v2 path — Kind = "approval"
    // -------------------------------------------------------------------

    private static string BuildLegacyApprovalText(ToolInteractionRequest request)
    {
        var lines = new List<string>
        {
            ":lock: *Tool approval required*",
            $"> `{request.ToolName}`: `{request.DisplayText}`",
            BuildLegacyApproveHeader(request)
        };

        var verbs = ResolveDisplayVerbs(request);
        if (verbs.Count > 1)
        {
            foreach (var verb in verbs)
                lines.Add($"  • `{verb}`");
        }

        if (request.IsMessy)
            lines.Add(ComplexCommandHint);

        AppendAdoptedContextSummary(lines, request);

        lines.Add("");
        lines.Add("Reply with:");
        foreach (var replyOption in EnumerateReplyOptions(request.Options))
            lines.Add($"  *{replyOption.Letter})* {replyOption.Option.Label}");

        return string.Join("\n", lines);
    }

    private static IReadOnlyList<Block> BuildLegacyApprovalBlocks(ToolInteractionRequest request)
    {
        var blocks = new List<Block>
        {
            new SectionBlock
            {
                Text = new Markdown(":lock: *Tool approval required*")
            },
            new SectionBlock
            {
                Text = new Markdown($"*Tool:* `{EscapeMarkdown(request.ToolName)}`\n*Request:* `{EscapeMarkdown(request.DisplayText)}`"),
                Expand = true
            },
            new SectionBlock
            {
                Text = new Markdown($"*{EscapeMarkdown(BuildLegacyApproveHeader(request))}*")
            }
        };

        var verbs = ResolveDisplayVerbs(request);
        if (verbs.Count > 1)
        {
            var verbLines = verbs.Select(v => $"• `{EscapeMarkdown(v)}`");
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(string.Join("\n", verbLines))
            });
        }

        if (request.IsMessy)
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(ComplexCommandHint)
            });
        }

        if (request.HasAdoptedContext)
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(BuildAdoptedContextMarkdown(request))
            });
        }

        blocks.Add(BuildActionsBlock(request));
        blocks.Add(BuildReplyHintBlock(request));

        return blocks;
    }

    // -------------------------------------------------------------------
    // Trust-zones zone-gate prompt — Kind = "approval_zone"
    // -------------------------------------------------------------------
    //
    // Shape per the trust-zones spec:
    //   Header: "Trust this path?" / "Trust these N paths?"
    //   Body:   the command being approved + the bulleted path list
    //   Row:    Once / Session / Trust <path-or-all> / Deny
    //
    // The "Trust" button replaces the legacy "Always here" label; its
    // key is still ApprovalOptionKeys.ApproveAlways so the response
    // handler decodes scope identically.

    private static string BuildZoneApprovalText(ToolInteractionRequest request)
    {
        var paths = ResolveZonePaths(request);

        var lines = new List<string>
        {
            ":lock: *Trust zone required*",
            $"> `{request.ToolName}`: `{request.DisplayText}`",
            BuildZoneHeader(paths.Count)
        };

        foreach (var path in paths)
            lines.Add($"  • `{path}`");

        AppendAdoptedContextSummary(lines, request);

        lines.Add("");
        lines.Add("Reply with:");
        foreach (var replyOption in EnumerateReplyOptions(request.Options))
            lines.Add($"  *{replyOption.Letter})* {RenderZoneButtonLabel(replyOption.Option, paths)}");

        return string.Join("\n", lines);
    }

    private static IReadOnlyList<Block> BuildZoneApprovalBlocks(ToolInteractionRequest request)
    {
        var paths = ResolveZonePaths(request);

        var blocks = new List<Block>
        {
            new SectionBlock
            {
                Text = new Markdown(":lock: *Trust zone required*")
            },
            new SectionBlock
            {
                Text = new Markdown(
                    $"*Tool:* `{EscapeMarkdown(request.ToolName)}`\n"
                    + $"*Request:* `{EscapeMarkdown(request.DisplayText)}`"),
                Expand = true
            },
            new SectionBlock
            {
                Text = new Markdown($"*{EscapeMarkdown(BuildZoneHeader(paths.Count))}*")
            }
        };

        if (paths.Count > 0)
        {
            var pathLines = paths.Select(p => $"• `{EscapeMarkdown(p)}`");
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(string.Join("\n", pathLines))
            });
        }

        if (request.HasAdoptedContext)
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(BuildAdoptedContextMarkdown(request))
            });
        }

        blocks.Add(BuildActionsBlock(request, option => RenderZoneButtonLabel(option, paths)));
        blocks.Add(BuildReplyHintBlock(request));

        return blocks;
    }

    private static string BuildZoneHeader(int pathCount) => pathCount switch
    {
        0 => "Trust this scope?",
        1 => "Trust this path?",
        _ => $"Trust these {pathCount} paths?"
    };

    /// <summary>
    /// Renders the dynamic label for the trust-button on a zone prompt.
    /// Single-path: "Trust /etc/nginx". Multi-path: "Trust all N listed".
    /// All other options keep their fixed
    /// <see cref="ApprovalOptionKeys"/> label.
    /// </summary>
    private static string RenderZoneButtonLabel(ToolInteractionOption option, IReadOnlyList<string> paths)
    {
        if (option.Key != ApprovalOptionKeys.ApproveAlways)
            return option.Label;

        if (paths.Count == 1)
            return TruncateButtonLabel($"Trust {paths[0]}");

        return TruncateButtonLabel($"Trust all {paths.Count} listed");
    }

    /// <summary>
    /// Untrusted paths flow through <see cref="ToolInteractionRequest.Patterns"/>
    /// for trust-zones prompts (the workflow dispatcher stages
    /// <see cref="Netclaw.Security.ZonePromptInfo.UntrustedPaths"/> there
    /// because the existing <see cref="ToolInteractionRequest"/> shape
    /// already had a path-shaped list field).
    /// </summary>
    private static IReadOnlyList<string> ResolveZonePaths(ToolInteractionRequest request)
        => request.Patterns;

    // -------------------------------------------------------------------
    // Trust-zones verb-gate prompt — Kind = "approval_verb"
    // -------------------------------------------------------------------
    //
    // Shape per the trust-zones spec:
    //   Header: "Approve this verb pattern?"
    //   Body:   the command being approved + the pattern row
    //   Row:    Once / Session / Always <pattern> / Always anywhere / Deny
    //
    // ApprovalOptionKeys.ApproveEverywhere stays on this prompt because
    // verb patterns DO have a global form (the verb pattern matches
    // regardless of cwd). Zone prompts don't expose Everywhere — a zone
    // is always a concrete directory.

    private static string BuildVerbApprovalText(ToolInteractionRequest request)
    {
        var pattern = ResolveVerbPattern(request);

        var lines = new List<string>
        {
            ":lock: *Verb approval required*",
            $"> `{request.ToolName}`: `{request.DisplayText}`",
            "Approve this verb pattern?",
            $"  • `{pattern}`"
        };

        AppendAdoptedContextSummary(lines, request);

        lines.Add("");
        lines.Add("Reply with:");
        foreach (var replyOption in EnumerateReplyOptions(request.Options))
            lines.Add($"  *{replyOption.Letter})* {RenderVerbButtonLabel(replyOption.Option, pattern)}");

        return string.Join("\n", lines);
    }

    private static IReadOnlyList<Block> BuildVerbApprovalBlocks(ToolInteractionRequest request)
    {
        var pattern = ResolveVerbPattern(request);

        var blocks = new List<Block>
        {
            new SectionBlock
            {
                Text = new Markdown(":lock: *Verb approval required*")
            },
            new SectionBlock
            {
                Text = new Markdown(
                    $"*Tool:* `{EscapeMarkdown(request.ToolName)}`\n"
                    + $"*Request:* `{EscapeMarkdown(request.DisplayText)}`\n"
                    + $"*Pattern:* `{EscapeMarkdown(pattern)}`"),
                Expand = true
            },
            new SectionBlock
            {
                Text = new Markdown("*Approve this verb pattern?*")
            }
        };

        if (request.HasAdoptedContext)
        {
            blocks.Add(new SectionBlock
            {
                Text = new Markdown(BuildAdoptedContextMarkdown(request))
            });
        }

        blocks.Add(BuildActionsBlock(request, option => RenderVerbButtonLabel(option, pattern)));
        blocks.Add(BuildReplyHintBlock(request));

        return blocks;
    }

    /// <summary>
    /// Renders the dynamic label for the Always-button on a verb prompt:
    /// "Always git push origin main *". Everywhere keeps its fixed
    /// "Always anywhere" label because the pattern is implicit
    /// ("anywhere" makes it clear).
    /// </summary>
    private static string RenderVerbButtonLabel(ToolInteractionOption option, string pattern)
    {
        if (option.Key != ApprovalOptionKeys.ApproveAlways)
            return option.Label;

        return TruncateButtonLabel($"Always {pattern}");
    }

    private static string ResolveVerbPattern(ToolInteractionRequest request)
        => request.CandidateVerbs.Count > 0
            ? request.CandidateVerbs[0]
            : string.Empty;

    // -------------------------------------------------------------------
    // Resolution line (post-click rendering)
    // -------------------------------------------------------------------

    /// <summary>
    /// Single-line resolution message replacing v1's dual <c>Patterns</c> /
    /// <c>Directory Roots</c> sections. Trust-zones-aware: zone prompts
    /// surface "Saved zone:" and verb prompts surface "Saved verb:" so
    /// the resolution mirrors the prompt shape rather than collapsing
    /// to the legacy v2 verbs-and-directory format.
    /// </summary>
    private static string BuildResolutionLine(ToolInteractionRequest request, string selectedKey)
    {
        return request.Kind switch
        {
            "approval_zone" => BuildZoneResolutionLine(request, selectedKey),
            "approval_verb" => BuildVerbResolutionLine(request, selectedKey),
            _ => BuildLegacyResolutionLine(request, selectedKey)
        };
    }

    private static string BuildZoneResolutionLine(ToolInteractionRequest request, string selectedKey)
    {
        var paths = ResolveZonePaths(request);
        var renderedPaths = paths.Count == 0
            ? "(no paths)"
            : string.Join(", ", paths);

        return selectedKey switch
        {
            ApprovalOptionKeys.ApproveAlways => $"Saved zone: {renderedPaths}",
            ApprovalOptionKeys.ApproveSession => $"Saved zone for this chat: {renderedPaths}",
            ApprovalOptionKeys.ApproveOnce => "Approved (no save)",
            ApprovalOptionKeys.Deny => "Denied",
            _ => "Resolved"
        };
    }

    private static string BuildVerbResolutionLine(ToolInteractionRequest request, string selectedKey)
    {
        var pattern = ResolveVerbPattern(request);

        return selectedKey switch
        {
            ApprovalOptionKeys.ApproveAlways => $"Saved verb: {pattern}",
            ApprovalOptionKeys.ApproveEverywhere => $"Saved verb (anywhere): {pattern}",
            ApprovalOptionKeys.ApproveSession => $"Saved verb for this chat: {pattern}",
            ApprovalOptionKeys.ApproveOnce => "Approved (no save)",
            ApprovalOptionKeys.Deny => "Denied",
            _ => "Resolved"
        };
    }

    private static string BuildLegacyResolutionLine(ToolInteractionRequest request, string selectedKey)
    {
        var verbs = string.Join(", ", ResolveDisplayVerbs(request));
        var location = ResolveLegacyHeaderLocation(request);

        return selectedKey switch
        {
            ApprovalOptionKeys.ApproveAlways => $"Saved: {verbs} in {location}",
            ApprovalOptionKeys.ApproveEverywhere => $"Saved: {verbs} anywhere",
            ApprovalOptionKeys.ApproveSession => $"Saved for this chat: {verbs} in {location}",
            ApprovalOptionKeys.ApproveOnce => "Approved (no save)",
            ApprovalOptionKeys.Deny => "Denied",
            _ => "Resolved"
        };
    }

    // -------------------------------------------------------------------
    // Shared block construction
    // -------------------------------------------------------------------

    private static ActionsBlock BuildActionsBlock(
        ToolInteractionRequest request,
        Func<ToolInteractionOption, string>? labelOverride = null)
    {
        // Slack hard-caps PlainText button text at 76 characters; oversized
        // labels are rejected with `invalid_blocks` and the post fails.
        // Static labels from ApprovalOptionKeys are within the cap by
        // construction. Dynamic labels (Trust <path>, Always <pattern>)
        // run through TruncateButtonLabel.
        return new ActionsBlock
        {
            Elements = [.. request.Options
                .Select(option =>
                {
                    var label = labelOverride is not null ? labelOverride(option) : option.Label;
                    return (IActionElement)new Button
                    {
                        ActionId = BuildActionId(option.Key),
                        Text = new PlainText(label),
                        Value = BuildButtonValue(request, option),
                        Style = GetButtonStyle(option.Key),
                        AccessibilityLabel = label
                    };
                })]
        };
    }

    private static SectionBlock BuildReplyHintBlock(ToolInteractionRequest request) => new()
    {
        Text = new Markdown($"You can also reply with {FormatReplyLetters(request.Options)} in this thread.")
    };

    /// <summary>
    /// Truncates a button label to <see cref="ApprovalOptionKeys.MaxLabelLength"/>
    /// with a trailing ellipsis when over the cap. The full path/pattern
    /// is always preserved in the prompt body, so the truncation is
    /// purely cosmetic — the user can still see what they're trusting
    /// before they click.
    /// </summary>
    private static string TruncateButtonLabel(string label)
    {
        const string Ellipsis = "…";

        if (label.Length <= ApprovalOptionKeys.MaxLabelLength)
            return label;

        // Reserve one char for the ellipsis itself.
        var keep = ApprovalOptionKeys.MaxLabelLength - Ellipsis.Length;
        return label[..keep] + Ellipsis;
    }

    // -------------------------------------------------------------------
    // Legacy v2 header (unchanged behavior)
    // -------------------------------------------------------------------

    /// <summary>
    /// Builds the legacy v2 prompt header: "Approve git status in /repo/?".
    /// Single-verb invocations name the verb; multi-verb invocations use
    /// the generic "Approve in &lt;dir&gt;?" form and rely on the bullet
    /// list to enumerate verbs. Only used for Kind = "approval".
    /// </summary>
    private static string BuildLegacyApproveHeader(ToolInteractionRequest request)
    {
        var verbs = ResolveDisplayVerbs(request);
        var location = ResolveLegacyHeaderLocation(request);

        return verbs.Count == 1
            ? $"Approve {verbs[0]} in {location}?"
            : $"Approve in {location}?";
    }

    private static string ResolveLegacyHeaderLocation(ToolInteractionRequest request)
    {
        var distinctDirs = request.Candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Directory))
            .Select(c => c.Directory!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (distinctDirs.Count == 1)
            return distinctDirs[0];

        if (distinctDirs.Count > 1)
            return $"{distinctDirs.Count} directories";

        if (string.IsNullOrWhiteSpace(request.Cwd))
            return "(no working directory)";

        return IsSessionScratchPath(request.Cwd) ? "this session" : request.Cwd;
    }

    private static bool IsSessionScratchPath(string cwd)
    {
        var normalized = cwd.Replace('\\', '/');
        return normalized.Contains("/.netclaw/sessions/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the verbs to display in the prompt body / resolution
    /// message for legacy v2 prompts. Prefers
    /// <see cref="ToolInteractionRequest.CandidateVerbs"/>; falls back
    /// to <c>Patterns</c> for messy commands where the matcher returned
    /// nothing.
    /// </summary>
    private static IReadOnlyList<string> ResolveDisplayVerbs(ToolInteractionRequest request)
        => request.CandidateVerbs.Count > 0
            ? request.CandidateVerbs
            : request.Patterns;

    // -------------------------------------------------------------------
    // Shared helpers (adopted context, reply letters, button wire format)
    // -------------------------------------------------------------------

    private static void AppendAdoptedContextSummary(List<string> lines, ToolInteractionRequest request)
    {
        if (!request.HasAdoptedContext)
            return;

        lines.Add($"Adopted context: present ({string.Join(", ", request.AdoptedSpeakerIds)})");
    }

    private static string BuildAdoptedContextMarkdown(ToolInteractionRequest request)
        => $"*Adopted context:* present\n*Speakers:* `{EscapeMarkdown(string.Join(", ", request.AdoptedSpeakerIds))}`";

    private static IEnumerable<(string Letter, ToolInteractionOption Option)> EnumerateReplyOptions(IReadOnlyList<ToolInteractionOption> options)
    {
        for (var i = 0; i < options.Count; i++)
            yield return (GetReplyLetter(i), options[i]);
    }

    private static string FormatReplyLetters(IReadOnlyList<ToolInteractionOption> options)
        => string.Join(", ", EnumerateReplyOptions(options).Select(static x => $"`{x.Letter}`"));

    private static string GetReplyLetter(int index)
        => ((char)('A' + index)).ToString();

    internal static string BuildButtonValue(ToolInteractionRequest request, ToolInteractionOption option)
        => ApprovalButtonValueCodec.Encode(request, option);

    internal static bool TryParseButtonValue(string? value, out string? callId, out string? selectedKey, out string? requesterSenderId)
        => ApprovalButtonValueCodec.TryDecode(value, out callId, out selectedKey, out requesterSenderId);

    private static ButtonStyle GetButtonStyle(string optionKey)
    {
        if (ApprovalOptionKeys.IsDangerStyled(optionKey))
            return ButtonStyle.Danger;
        if (optionKey == ApprovalOptionKeys.ApproveOnce)
            return ButtonStyle.Primary;
        return ButtonStyle.Default;
    }

    internal static bool IsApprovalActionId(string? actionId)
        => !string.IsNullOrWhiteSpace(actionId)
           && actionId.StartsWith($"{ApprovalActionId}_", StringComparison.Ordinal);

    private static string BuildActionId(string optionKey)
        => $"{ApprovalActionId}_{optionKey}";

    private static string EscapeMarkdown(string value)
        => value.Replace("`", "'", StringComparison.Ordinal);
}
