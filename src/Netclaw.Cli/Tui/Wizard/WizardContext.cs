// -----------------------------------------------------------------------
// <copyright file="WizardContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Providers;
using R3;

namespace Netclaw.Cli.Tui.Wizard;

/// <summary>
/// Shared state that flows between wizard steps. Replaces the flat properties
/// previously scattered across the monolithic InitWizardViewModel.
/// </summary>
public sealed class WizardContext : IDisposable
{
    /// <summary>Config and identity file paths.</summary>
    public required NetclawPaths Paths { get; init; }

    /// <summary>Registry of available LLM provider descriptors.</summary>
    public required ProviderDescriptorRegistry Registry { get; init; }

    /// <summary>
    /// Set by channel steps (Slack, Discord, etc.) to indicate at least one
    /// chat service is enabled. Used by the Channels step's <c>IsApplicable</c>
    /// to determine whether per-channel audience configuration should be shown.
    /// </summary>
    public bool AnyChatServicesEnabled { get; set; }

    /// <summary>
    /// Selected deployment posture from the SecurityPosture step.
    /// Read by channel steps to derive audience defaults.
    /// </summary>
    public DeploymentPosture? SelectedPosture { get; set; }

    /// <summary>
    /// Feature selections from the Feature Selection step.
    /// Null when posture is Personal (step is skipped).
    /// </summary>
    public FeatureSelections? FeatureSelections { get; set; }

    /// <summary>
    /// Per-channel audience entries keyed by channel source (e.g., "slack", "discord").
    /// Each channel step populates its own bucket in <c>OnLeave</c>.
    /// The Channels step renders all entries grouped by source.
    /// This allows DM entries and channel entries from different platforms to be
    /// configured independently (e.g., Slack DMs vs Discord DMs).
    /// </summary>
    public Dictionary<ChannelType, List<ChannelEntry>> ChannelEntries { get; } = [];

    /// <summary>Shared status message displayed at the bottom of the wizard.</summary>
    public ReactiveProperty<string> StatusMessage { get; } = new("");

    /// <summary>Request a terminal redraw from the Termina framework.</summary>
    public required Action RequestRedraw { get; init; }

    /// <summary>
    /// Null for a fresh install. When populated, init-owned editors that
    /// implement <see cref="Sections.ISectionEditor"/> SHALL prefill their
    /// non-secret fields from this snapshot so re-running init does not
    /// force operators to retype values they already configured.
    /// </summary>
    /// <remarks>
    /// <para><b>What this enables:</b> init-owned re-entry against an existing
    /// install — for example, re-running <c>netclaw init</c> on an already
    /// configured host prefills the Identity step's agent name / timezone
    /// and the SecurityPosture step's current posture.</para>
    /// <para><b>What this does NOT enable:</b> turning <c>netclaw init</c>
    /// into the long-term editor for ongoing settings. The locked product
    /// split keeps ongoing edits in <c>netclaw config</c>; this field
    /// supports re-running bootstrap-shaped flows only.</para>
    /// <para><b>Secret handling:</b> stored secrets SHALL NOT appear here.
    /// The dictionary is loaded from <c>netclaw.json</c> exclusively; the
    /// only references to secret values are presence-only probes via
    /// <see cref="Netclaw.Cli.Config.ConfigFileHelper.SecretPresent"/>.</para>
    /// </remarks>
    public IReadOnlyDictionary<string, object>? ExistingConfig { get; init; }

    public void Dispose()
    {
        StatusMessage.Dispose();
    }
}

/// <summary>
/// Deployment-wide feature toggle selections from the Feature Selection wizard step.
/// </summary>
public sealed class FeatureSelections
{
    public bool MemoryEnabled { get; set; }
    public bool SearchEnabled { get; set; }
    public bool SkillsEnabled { get; set; }
    public bool SchedulingEnabled { get; set; }
    public bool SubAgentsEnabled { get; set; }
    public bool WebhooksEnabled { get; set; }
}
