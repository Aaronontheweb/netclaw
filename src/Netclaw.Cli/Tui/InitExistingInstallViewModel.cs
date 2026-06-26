// -----------------------------------------------------------------------
// <copyright file="InitExistingInstallViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Follow-up action requested from the existing-install menu that the init host
/// cannot service itself and must hand back to <c>Program</c> after the TUI exits
/// (mirrors <see cref="ConfigDashboardAction"/> / RunDoctor). In-host destinations
/// (the wizard, identity redo) are reached by routing instead.
/// </summary>
public enum InitFollowUpAction
{
    None,
    OpenConfigEditor,
}

/// <summary>Shared state carrying the existing-install menu's deferred action out of
/// the Termina host so <c>Program</c> can dispatch it.</summary>
public sealed class InitNavigationState
{
    public InitFollowUpAction PendingAction { get; set; }
}

/// <summary>
/// Existing-install menu shown when <c>netclaw init</c> runs against a config that
/// already exists (simplify-netclaw-init). Instead of silently re-walking setup or
/// refusing with a hidden <c>--force</c>, it offers an explicit action menu and an
/// in-place, double-confirmed start-over flow. Config is untouched until the operator
/// confirms a destructive action.
/// </summary>
public sealed class InitExistingInstallViewModel : ReactiveViewModel
{
    public enum Phase
    {
        Menu,
        ResetScope,
        ResetConfirm1,
        ResetConfirm2,
        Progress,
    }

    public enum ResetScopeKind
    {
        SetupOnly,
        Full,
    }

    public sealed record MenuItem(string Label, string Description);

    private readonly NetclawPaths _paths;
    private readonly InitNavigationState _navigationState;
    private readonly DaemonManager _daemonManager;

    public InitExistingInstallViewModel(
        NetclawPaths paths,
        InitNavigationState navigationState,
        DaemonManager daemonManager)
    {
        _paths = paths;
        _navigationState = navigationState;
        _daemonManager = daemonManager;
    }

    /// <summary>Route the wizard launches for "Redo identity setup".</summary>
    public const string IdentityRoute = "/init/identity";

    /// <summary>Route launched for a fresh setup after a confirmed reset.</summary>
    public const string WizardRoute = "/init";

    /// <summary>This menu's own route (identity redo returns here on Esc).</summary>
    public const string MenuRoute = "/init/menu";

    public ReactiveProperty<Phase> CurrentPhase { get; } = new(Phase.Menu);
    public ReactiveProperty<int> SelectedIndex { get; } = new(0);
    public ReactiveProperty<string> StatusMessage { get; } = new("");

    private ResetScopeKind _scope = ResetScopeKind.SetupOnly;
    public ResetScopeKind Scope => _scope;

    // ── Progress-screen state ──

    private readonly List<bool> _completedSteps = [];
    public IReadOnlyList<bool> CompletedSteps => _completedSteps;

    public ReactiveProperty<int> CurrentProgressStep { get; } = new(-1);
    public ReactiveProperty<string> ProgressMessage { get; } = new("");

    public static readonly IReadOnlyList<MenuItem> MenuItems =
    [
        new("Redo identity setup", "Re-run just the identity step; provider and settings are kept."),
        new("Open configuration editor", "Adjust settings in `netclaw config` instead."),
        new("Start over from scratch", "Reset and run the whole setup again."),
        new("Cancel", "Leave everything as-is and exit."),
    ];

    public static readonly IReadOnlyList<MenuItem> ScopeItems =
    [
        new("Reset setup only", "Re-run setup; keep memory, sessions, and skills."),
        new("Full reset", "Delete ALL Netclaw data: config, memory, sessions, secrets."),
        new("Cancel", "Go back without changing anything."),
    ];

    /// <summary>Items for the current phase (drives the rendered list).</summary>
    public IReadOnlyList<MenuItem> CurrentItems => CurrentPhase.Value switch
    {
        Phase.Menu => MenuItems,
        Phase.ResetScope => ScopeItems,
        _ => ConfirmItems(),
    };

    private IReadOnlyList<MenuItem> ConfirmItems()
    {
        var full = _scope == ResetScopeKind.Full;
        return
        [
            new("Cancel", "Go back without changing anything."),
            new(full ? "Yes, delete everything" : "Yes, reset setup",
                full
                    ? "Permanently deletes config, memory, sessions, and secrets. Cannot be undone."
                    : "Re-runs setup. Memory, sessions, and skills are kept."),
        ];
    }

    public void MoveSelection(int delta)
    {
        var count = CurrentItems.Count;
        if (count == 0)
            return;

        var next = Math.Clamp(SelectedIndex.Value + delta, 0, count - 1);
        if (next != SelectedIndex.Value)
            SelectedIndex.Value = next;
    }

    /// <summary>Enter on the highlighted row.</summary>
    public void ActivateSelected()
    {
        switch (CurrentPhase.Value)
        {
            case Phase.Menu:
                ActivateMenu(SelectedIndex.Value);
                break;
            case Phase.ResetScope:
                ActivateScope(SelectedIndex.Value);
                break;
            case Phase.ResetConfirm1:
                ActivateConfirm(first: true);
                break;
            case Phase.ResetConfirm2:
                ActivateConfirm(first: false);
                break;
        }
    }

    private void ActivateMenu(int index)
    {
        switch (index)
        {
            case 0: // Redo identity setup
                Navigate?.Invoke(IdentityRoute);
                break;
            case 1: // Open configuration editor
                _navigationState.PendingAction = InitFollowUpAction.OpenConfigEditor;
                Shutdown();
                break;
            case 2: // Start over from scratch
                EnterPhase(Phase.ResetScope);
                break;
            default: // Cancel
                Shutdown();
                break;
        }
    }

    private void ActivateScope(int index)
    {
        switch (index)
        {
            case 0:
                _scope = ResetScopeKind.SetupOnly;
                EnterPhase(Phase.ResetConfirm1);
                break;
            case 1:
                _scope = ResetScopeKind.Full;
                EnterPhase(Phase.ResetConfirm1);
                break;
            default: // Cancel
                EnterPhase(Phase.Menu);
                break;
        }
    }

    // Confirm rows are [Cancel, Yes]. Default selection is Cancel (index 0), so a stray
    // Enter never deletes — the operator must move to "Yes" and confirm twice.
    private void ActivateConfirm(bool first)
    {
        if (SelectedIndex.Value == 0) // Cancel
        {
            EnterPhase(Phase.ResetScope);
            return;
        }

        if (first)
        {
            EnterPhase(Phase.ResetConfirm2);
            return;
        }

        StartResetProgress();
    }

    private void StartResetProgress()
    {
        CurrentPhase.Value = Phase.Progress;
        _completedSteps.Clear();
        _completedSteps.Add(false); // Stopping daemon
        _completedSteps.Add(false); // Deleting data
        _completedSteps.Add(false); // Complete
        CurrentProgressStep.Value = 0;
        ProgressMessage.Value = "Stopping daemon…";
        RequestRedraw();
        _ = RunResetAsync();
    }

    private async Task RunResetAsync()
    {
        // Step 1: Stop the daemon so file handles are released
        try
        {
            await _daemonManager.StopAsync("factory-reset");
        }
        catch
        {
            // Best-effort: daemon may not be running or may be managed externally.
        }

        _completedSteps[0] = true;
        CurrentProgressStep.Value = 1;
        ProgressMessage.Value = _scope == ResetScopeKind.Full
            ? "Deleting all data…"
            : "Deleting setup files…";
        RequestRedraw();

        // Step 2: Delete the directories
        try
        {
            if (_scope == ResetScopeKind.Full)
            {
                DeleteDirectory(_paths.BasePath);
            }
            else
            {
                DeleteDirectory(_paths.ConfigDirectory);
                DeleteDirectory(_paths.IdentityDirectory);
                DeleteDirectory(_paths.SoulDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProgressMessage.Value = $"Reset failed: {ex.Message}";
            RequestRedraw();
            return;
        }

        // Step 3: Mark complete and navigate to fresh wizard
        _completedSteps[2] = true;
        CurrentProgressStep.Value = 2;
        ProgressMessage.Value = "Purge complete";
        RequestRedraw();
        await Task.Delay(600); // Brief pause so the user sees the completion checkmark
        Navigate?.Invoke(WizardRoute);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>Esc: step back one phase, or quit from the menu.</summary>
    public void GoBack()
    {
        switch (CurrentPhase.Value)
        {
            case Phase.Menu:
                Shutdown();
                break;
            case Phase.ResetScope:
                EnterPhase(Phase.Menu);
                break;
            case Phase.ResetConfirm1:
                EnterPhase(Phase.ResetScope);
                break;
            case Phase.ResetConfirm2:
                EnterPhase(Phase.ResetConfirm1);
                break;
            case Phase.Progress:
                // Don't allow backing out of progress — it's running.
                break;
        }
    }

    private void EnterPhase(Phase phase)
    {
        CurrentPhase.Value = phase;
        // Confirm phases default to Cancel (index 0); menus/scope start at the top.
        SelectedIndex.Value = 0;
        StatusMessage.Value = "";
        RequestRedraw();
    }

    public void RequestQuit() => Shutdown();

    public override void Dispose()
    {
        CurrentPhase.Dispose();
        SelectedIndex.Dispose();
        StatusMessage.Dispose();
        CurrentProgressStep.Dispose();
        ProgressMessage.Dispose();
        base.Dispose();
    }
}
