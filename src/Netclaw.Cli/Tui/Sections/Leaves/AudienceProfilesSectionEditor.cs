// -----------------------------------------------------------------------
// <copyright file="AudienceProfilesSectionEditor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Json;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Sections.Leaves;

/// <summary>
/// Curated Audience Profiles editor. Per spec §11 / Requirement
/// "Audience Profiles is curated and excludes MCP editing", this leaf
/// exposes ONLY:
/// <list type="bullet">
///   <item>Tool Access (non-MCP) — the per-audience <c>AllowedTools</c> list.</item>
///   <item>File Access — <c>ReadFiles</c> / <c>WriteFiles</c> roots.</item>
///   <item>Incoming Attachments — <c>ChannelAttachments</c> policy.</item>
///   <item>Reset to posture default — resets the FULL underlying profile,
///     including hidden MCP and approval settings.</item>
/// </list>
/// It SHALL NOT expose per-audience runtime feature toggles, per-audience
/// shell mode, MCP grants/access editing, or raw approval-policy editing.
/// MCP permission edits route to <c>netclaw mcp permissions</c>.
/// </summary>
public sealed class AudienceProfilesSectionEditor : ISectionEditor
{
    public string SectionId => SectionIds.AudienceProfiles;
    public string DisplayName => "Audience Profiles";
    public string? Category => "Security & Access";
    public bool ShowInMenu => true;

    public IReadOnlyList<Type> RelevantDoctorChecks { get; } =
    [
        typeof(ToolAudienceProfilesDoctorCheck),
    ];

    public SectionStatus GetStatus(SectionEditorContext context)
    {
        return SectionConfigLookup.SectionExists(context, "Tools")
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;
    }

    public string Summary(SectionEditorContext context)
    {
        return SectionConfigLookup.SectionExists(context, "Tools")
            ? "configured per posture"
            : "(posture defaults only)";
    }

    public IWizardStepViewModel CreateEditor(WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Spec §11 marks shell mode + MCP grants + approval policy as
        // forbidden surfaces this editor MUST preserve. We deserialize the
        // entire Tools section from on-disk state into a typed ToolConfig
        // so every hidden field round-trips. Source: WizardContext.ExistingConfig
        // when the host already loaded it (init flow), otherwise read the
        // file ourselves so the dashboard single-step path also benefits.
        var existingTools = TryReadToolsSection(context);

        return new AudienceProfilesStepViewModel
        {
            ExistingShellMode = existingTools?.ShellMode ?? ShellExecutionMode.Off,
            ExistingProfiles = existingTools?.AudienceProfiles,
        };
    }

    /// <summary>
    /// Resolve the operator's saved <see cref="ToolConfig"/>:
    /// prefer <paramref name="context"/>'s pre-loaded <c>ExistingConfig</c>,
    /// fall back to reading <c>netclaw.json</c> directly so the
    /// preservation guarantee holds in dashboard single-step hosting
    /// (which does not necessarily populate <c>ExistingConfig</c>).
    /// Returns null only when no Tools section exists on disk; a
    /// present-but-malformed Tools section bubbles <see cref="JsonException"/>
    /// rather than silently flattening to defaults.
    /// </summary>
    private static ToolConfig? TryReadToolsSection(WizardContext context)
    {
        if (context.ExistingConfig is { } loaded
            && loaded.TryGetValue("Tools", out var fromCtx)
            && fromCtx is not null)
        {
            return DeserializeTools(fromCtx);
        }

        if (File.Exists(context.Paths.NetclawConfigPath))
        {
            var onDisk = ConfigFileHelper.LoadJsonDict(context.Paths.NetclawConfigPath);
            if (onDisk.TryGetValue("Tools", out var raw) && raw is not null)
                return DeserializeTools(raw);
        }

        return null;
    }

    private static ToolConfig? DeserializeTools(object raw)
    {
        // Round-trip through JsonSerializer so the rich ToolConfig shape
        // (nested ToolAudienceProfiles + ApprovalPolicy + McpServerToolGrants)
        // hydrates correctly regardless of whether the input is a nested
        // Dictionary<string, object> or a JsonElement tree. Use
        // JsonDefaults.ConfigRead so enum-as-string values like
        // ShellMode="HostAllowed" and case-insensitive keys round-trip
        // exactly the way the config file loader handles them. A
        // JsonException here is a real corruption signal and SHALL
        // propagate — falling back to defaults would destroy forbidden
        // surfaces on save.
        var json = JsonSerializer.Serialize(raw);
        return JsonSerializer.Deserialize<ToolConfig>(json, JsonDefaults.ConfigRead);
    }
}

/// <summary>
/// Step viewmodel for the curated Audience Profiles editor. Deliberately
/// thin — the editor surface is limited to four operator-facing concerns
/// per the spec, and the underlying mutation logic centralizes on
/// <see cref="AudienceProfilesPolicy"/> so tests can exercise the
/// allowed-vs-forbidden-edits contract without touching Termina.
/// </summary>
public sealed class AudienceProfilesStepViewModel : IWizardStepViewModel
{
    public string StepId => "audience-profiles";
    public string DisplayTitle => "Audience Profiles";

    /// <summary>Selected audience for the current edit pass.</summary>
    public TrustAudience Audience { get; set; } = TrustAudience.Personal;

    /// <summary>Pending allow-list of non-MCP tool names for the audience.</summary>
    public List<string> AllowedTools { get; } = new();

    /// <summary>Pending readable filesystem roots for the audience.</summary>
    public List<string> ReadFileRoots { get; } = new();

    /// <summary>Pending writable filesystem roots for the audience.</summary>
    public List<string> WriteFileRoots { get; } = new();

    /// <summary>Pending channel attachment policy for the audience.</summary>
    public ChannelAttachmentPolicy ChannelAttachments { get; set; } = ChannelAttachmentPolicy.Empty;

    /// <summary>True when the operator chose Reset to posture default for the audience.</summary>
    public bool ResetToPostureDefault { get; set; }

    /// <summary>
    /// Existing shell-mode from on-disk Tools section, captured at editor
    /// construction. Required so <see cref="ContributeConfig"/> does NOT
    /// silently clobber the operator's prior ShellMode to <c>Off</c> on
    /// save — per spec §11, shell mode is a forbidden surface for this
    /// editor and SHALL be preserved.
    /// </summary>
    public ShellExecutionMode ExistingShellMode { get; init; } = ShellExecutionMode.Off;

    /// <summary>
    /// Existing AudienceProfiles tree from disk. Null when no Tools section
    /// is configured yet. Used as the starting state so prior MCP grants /
    /// approval policy / per-audience hidden fields survive a curated edit.
    /// </summary>
    public ToolAudienceProfiles? ExistingProfiles { get; init; }

    public bool IsApplicable(WizardContext context) => true;
    public int CurrentSubStep => 0;
    public int SubStepCount => 1;
    public string GetHelpText() =>
        "  Tool Access (non-MCP), File Access, Incoming Attachments. " +
        "MCP permissions live in `netclaw mcp permissions`.";
    public bool TryAdvance() => false;
    public bool TryGoBack() => false;
    public void OnEnter(WizardContext context, NavigationDirection direction) { }
    public void OnLeave() { }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        // Start from the operator's existing profiles (if any) so hidden
        // MCP / approval / shell-mode fields the curated editor SHALL NOT
        // touch survive the save. Falling back to posture defaults only
        // when there is genuinely nothing on disk avoids clobbering.
        var profiles = builder.Tools?.AudienceProfiles
            ?? ExistingProfiles
            ?? ToolAudienceProfileDefaults.CreateProfiles();

        AudienceProfilesPolicy.Apply(
            profiles,
            Audience,
            AllowedTools,
            ReadFileRoots,
            WriteFileRoots,
            ChannelAttachments,
            ResetToPostureDefault);

        // Preserve the existing shell mode from disk when the builder
        // doesn't already have one. Without this, a config-mode single
        // step that runs Audience Profiles in isolation would silently
        // reset ShellMode to Off.
        var shellMode = builder.Tools?.ShellMode ?? ExistingShellMode;
        builder.Tools = new ToolConfig
        {
            ShellMode = shellMode,
            AudienceProfiles = profiles,
        };
    }

    public void ContributeSecrets(WizardSecretsBuilder builder) { }
    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct) =>
        Task.CompletedTask;
    public void Dispose() { }
}

/// <summary>
/// Mutation policy for the curated Audience Profiles editor. Extracted so
/// tests can verify the "edits ONLY the visible slice; Reset resets the
/// FULL underlying profile (including hidden MCP / approval)" rule
/// without spinning up a wizard.
/// </summary>
public static class AudienceProfilesPolicy
{
    /// <summary>
    /// Apply the curated edits to <paramref name="profiles"/>. When
    /// <paramref name="resetToPostureDefault"/> is true, the entire
    /// underlying profile for <paramref name="audience"/> is replaced with
    /// the posture default (including hidden MCP / approval settings); the
    /// curated edits are then ignored.
    /// </summary>
    public static void Apply(
        ToolAudienceProfiles profiles,
        TrustAudience audience,
        IReadOnlyList<string> allowedTools,
        IReadOnlyList<string> readFileRoots,
        IReadOnlyList<string> writeFileRoots,
        ChannelAttachmentPolicy channelAttachments,
        bool resetToPostureDefault)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(allowedTools);
        ArgumentNullException.ThrowIfNull(readFileRoots);
        ArgumentNullException.ThrowIfNull(writeFileRoots);
        ArgumentNullException.ThrowIfNull(channelAttachments);

        if (resetToPostureDefault)
        {
            // Reset to posture default SHALL reset the FULL underlying
            // profile — including hidden MCP and approval settings — per
            // spec scenario "Reset to posture default resets full
            // underlying profile". We replace the entire profile object.
            ReplaceProfile(profiles, audience, BuildPostureDefault(audience));
            return;
        }

        var target = ResolveProfile(profiles, audience);

        // Tool Access (non-MCP): only AllowedTools is exposed. We
        // intentionally do NOT touch AllowedMcpServers /
        // McpServerToolGrants — those edits route to `netclaw mcp
        // permissions`.
        target.AllowedTools = [.. allowedTools];

        // File Access: ReadFiles / WriteFiles roots. Mode is set to Roots
        // when any root is provided, otherwise preserved.
        if (readFileRoots.Count > 0)
        {
            target.ReadFiles.Roots = [.. readFileRoots];
            target.ReadFiles.Mode = ToolFilesystemMode.Roots;
        }
        if (writeFileRoots.Count > 0)
        {
            target.WriteFiles.Roots = [.. writeFileRoots];
            target.WriteFiles.Mode = ToolFilesystemMode.Roots;
        }

        // Incoming Attachments: ChannelAttachments policy.
        target.ChannelAttachments = channelAttachments;

        // Forbidden surfaces (per-audience runtime features, shell mode,
        // MCP grants, raw approval-policy editing) are NOT touched here
        // and SHALL NOT be touched. If anything in those fields existed on
        // disk, semantic merge preserves it.
    }

    private static ToolAudienceProfile ResolveProfile(
        ToolAudienceProfiles profiles, TrustAudience audience) =>
        audience switch
        {
            TrustAudience.Public => profiles.Public,
            TrustAudience.Team => profiles.Team,
            TrustAudience.Personal => profiles.Personal,
            _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "Unknown audience."),
        };

    private static void ReplaceProfile(
        ToolAudienceProfiles profiles, TrustAudience audience, ToolAudienceProfile fresh)
    {
        switch (audience)
        {
            case TrustAudience.Public: profiles.Public = fresh; return;
            case TrustAudience.Team: profiles.Team = fresh; return;
            case TrustAudience.Personal: profiles.Personal = fresh; return;
            default: throw new ArgumentOutOfRangeException(nameof(audience), audience, "Unknown audience.");
        }
    }

    private static ToolAudienceProfile BuildPostureDefault(TrustAudience audience) =>
        audience switch
        {
            TrustAudience.Public => ToolAudienceProfileDefaults.CreatePublic(),
            TrustAudience.Team => ToolAudienceProfileDefaults.CreateTeam(),
            TrustAudience.Personal => ToolAudienceProfileDefaults.CreatePersonal(),
            _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "Unknown audience."),
        };
}
