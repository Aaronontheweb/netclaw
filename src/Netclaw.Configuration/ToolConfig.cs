// -----------------------------------------------------------------------
// <copyright file="ToolConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>
/// Shared configuration for first-party tool execution.
/// </summary>
public sealed class ToolConfig
{
    public ShellExecutionMode? ShellMode { get; set; }

    /// <summary>
    /// The capture ceiling: the maximum characters of tool output captured (in
    /// bounded memory) to become the spill body written to a session file. It is
    /// NOT the inline budget — <c>SessionTuning.MaxInlineToolResultChars</c> (<c>N</c>)
    /// owns what the model sees inline. Output beyond this ceiling is drained-and-
    /// discarded (the source keeps draining so a live child never deadlocks) and the
    /// spill is a head+tail view. Sized so the spill is useful while staying
    /// redactable in a single in-memory pass.
    /// </summary>
    public int MaxOutputChars { get; set; } = 256_000;

    public ToolAudienceProfiles AudienceProfiles { get; set; } = new();
    public WebFetchConfig WebFetch { get; set; } = new();

    /// <summary>
    /// Additional shell command patterns to add to the hard deny list.
    /// These are verb-chain prefixes that are categorically blocked
    /// and cannot be approved. Added to the compiled-in defaults.
    /// </summary>
    public List<string> HardDenyPatterns { get; set; } = [];

    /// <summary>
    /// Binds the daemon <c>Tools</c> section and validates the channel attachment policy.
    /// A configured list replaces the default list, so an operator can narrow tool grants,
    /// read roots, attachment categories, and the HTTP allow list. See
    /// <see cref="ConfigurationListBinder"/> for the list rules. The caller must log each
    /// item in <paramref name="warnings"/> at startup.
    /// </summary>
    public static ToolConfig BindFromConfiguration(IConfigurationSection section, out IReadOnlyList<string> warnings)
    {
        var toolConfig = ConfigurationListBinder.Get<ToolConfig>(section, out var bindingWarnings);
        var allWarnings = new List<string>(bindingWarnings);
        MapLegacyDefaultAllowedTools(toolConfig.AudienceProfiles.Public, TrustAudience.Public, allWarnings);
        MapLegacyDefaultAllowedTools(toolConfig.AudienceProfiles.Team, TrustAudience.Team, allWarnings);
        warnings = allWarnings;

        var attachmentErrors = toolConfig.AudienceProfiles.ValidateChannelAttachments();
        if (attachmentErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid Tools.AudienceProfiles.ChannelAttachments configuration: "
                + string.Join("; ", attachmentErrors));
        }

        return toolConfig;
    }

    // `netclaw init` wrote the complete default list, and the old binder added the current
    // defaults to it. Replacement binding would silently remove tools that later releases
    // added to the default, such as tool_output_read. Only an exact older default list maps to
    // the current default. A list that differs in any way is operator intent, so the daemon
    // applies it as written and never widens it.
    private static void MapLegacyDefaultAllowedTools(
        ToolAudienceProfile profile,
        TrustAudience audience,
        List<string> warnings)
    {
        if (profile.ToolsMode != ToolProfileMode.Allowlist
            || !ToolAudienceProfileDefaults.IsLegacyDefaultAllowedTools(audience, profile.AllowedTools))
        {
            return;
        }

        warnings.Add(ToolAudienceProfileDefaults.DescribeLegacyDefaultAllowedTools(audience, profile.AllowedTools));
        profile.AllowedTools = [.. ToolAudienceProfileDefaults.CurrentDefaultAllowedTools(audience)];
    }
}
