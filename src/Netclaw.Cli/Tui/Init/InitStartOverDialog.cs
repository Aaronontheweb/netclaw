// -----------------------------------------------------------------------
// <copyright file="InitStartOverDialog.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui.Init;

/// <summary>
/// Start-over sub-dialog shown when the operator picks
/// <see cref="InitMenuAction.StartOver"/>. Three choices in spec-locked
/// order: <c>Reset setup only</c>, <c>Full reset</c>, <c>Cancel</c>.
/// Both destructive choices require a double confirmation before any
/// file is touched.
/// </summary>
public static class InitStartOverDialog
{
    /// <summary>The canonical three choices in spec-locked order.</summary>
    public static IReadOnlyList<InitStartOverChoice> Choices { get; } = new[]
    {
        new InitStartOverChoice(
            "reset-setup",
            "Reset setup only",
            "Wipes netclaw.json, secrets.json, identity files, and seeded agents. " +
            "Preserves workspaces, sessions, memory, and skills.",
            InitStartOverAction.ResetSetup),

        new InitStartOverChoice(
            "full-reset",
            "Full reset",
            "Wipes EVERYTHING under the netclaw root directory. " +
            "Includes workspaces, sessions, memory, skills, and the daemon database.",
            InitStartOverAction.FullReset),

        new InitStartOverChoice(
            "cancel",
            "Cancel",
            "Return to the existing-install menu without making any changes.",
            InitStartOverAction.Cancel),
    };

    /// <summary>Look up a start-over choice by id; throws when not found.</summary>
    public static InitStartOverChoice Resolve(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Choices.FirstOrDefault(c => c.Id == id)
            ?? throw new KeyNotFoundException($"Unknown start-over choice '{id}'.");
    }

    /// <summary>True if the action requires a double confirmation before execution.</summary>
    public static bool RequiresDoubleConfirmation(InitStartOverAction action) =>
        action is InitStartOverAction.ResetSetup or InitStartOverAction.FullReset;
}

/// <summary>One option on the start-over sub-dialog.</summary>
public sealed record InitStartOverChoice(
    string Id,
    string Label,
    string Description,
    InitStartOverAction Action);

/// <summary>Destructive action to take when a start-over choice is confirmed.</summary>
public enum InitStartOverAction
{
    /// <summary>Wipe configuration + identity files; preserve workspaces, sessions, memory, skills.</summary>
    ResetSetup,

    /// <summary>Wipe the entire netclaw root directory.</summary>
    FullReset,

    /// <summary>Return to the prior menu without doing anything.</summary>
    Cancel,
}
