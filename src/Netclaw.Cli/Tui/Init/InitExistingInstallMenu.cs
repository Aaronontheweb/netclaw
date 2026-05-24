// -----------------------------------------------------------------------
// <copyright file="InitExistingInstallMenu.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Init;

/// <summary>
/// Existing-install menu shown by <c>netclaw init</c> when the configured
/// install is already present. Encodes the four options locked by
/// simplify-netclaw-init §3: <c>Redo identity setup</c>,
/// <c>Open configuration editor</c>, <c>Start over from scratch</c>,
/// <c>Cancel</c>.
/// </summary>
/// <remarks>
/// Decision and routing logic lives here (testable, no UI). The actual
/// rendering / selection prompt is the host's concern (Spectre.Console in
/// Program.cs); this type just describes the menu and resolves choices.
/// </remarks>
public static class InitExistingInstallMenu
{
    /// <summary>The canonical menu choices in spec-locked order.</summary>
    public static IReadOnlyList<InitMenuChoice> Choices { get; } = new[]
    {
        new InitMenuChoice("redo-identity", "Redo identity setup", InitMenuAction.RedoIdentity),
        new InitMenuChoice("open-config", "Open configuration editor", InitMenuAction.OpenConfig),
        new InitMenuChoice("start-over", "Start over from scratch", InitMenuAction.StartOver),
        new InitMenuChoice("cancel", "Cancel", InitMenuAction.Cancel),
    };

    /// <summary>
    /// True if an existing install is detected at <paramref name="paths"/>.
    /// The contract uses presence of <c>netclaw.json</c> as the signal —
    /// the file is the canonical install marker.
    /// </summary>
    public static bool IsExistingInstall(NetclawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return File.Exists(paths.NetclawConfigPath);
    }

    /// <summary>Look up a menu choice by id; throws when not found.</summary>
    public static InitMenuChoice Resolve(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Choices.FirstOrDefault(c => c.Id == id)
            ?? throw new KeyNotFoundException($"Unknown init menu choice '{id}'.");
    }
}

/// <summary>One option on the existing-install menu.</summary>
public sealed record InitMenuChoice(string Id, string Label, InitMenuAction Action);

/// <summary>Action to take when an existing-install menu choice is selected.</summary>
public enum InitMenuAction
{
    /// <summary>Re-run the init-owned identity flow only (single-step host).</summary>
    RedoIdentity,

    /// <summary>Hand off to `netclaw config` (exec or re-launch with config mode).</summary>
    OpenConfig,

    /// <summary>Open the start-over sub-dialog (Reset setup only / Full reset / Cancel).</summary>
    StartOver,

    /// <summary>Exit init without doing anything.</summary>
    Cancel,
}
