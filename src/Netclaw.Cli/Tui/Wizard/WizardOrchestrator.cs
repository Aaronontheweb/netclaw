// -----------------------------------------------------------------------
// <copyright file="WizardOrchestrator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Wizard.Steps;
using R3;

namespace Netclaw.Cli.Tui.Wizard;

/// <summary>
/// Hosting mode for <see cref="WizardOrchestrator"/>. Controls which
/// init-only writers run on save.
/// </summary>
public enum WizardHostingMode
{
    /// <summary>
    /// Full bootstrap flow: linear navigation across all applicable steps
    /// and, on save, write identity files, seed built-in agents, and bootstrap
    /// device pairing. Used by <c>netclaw init</c>.
    /// </summary>
    Init,

    /// <summary>
    /// Single-step hosting: one editor runs against existing state and saves
    /// without seeding agents or writing init-only side artifacts. Used by
    /// the future <c>netclaw config</c> command and by init-owned re-entry of
    /// a specific leaf editor.
    /// </summary>
    SingleStep,
}

/// <summary>
/// Thin orchestrator that manages wizard step sequencing, navigation,
/// and config finalization. Replaces the monolithic InitWizardViewModel's
/// step navigation and config writing responsibilities.
/// </summary>
public sealed class WizardOrchestrator : IDisposable
{
    private readonly IReadOnlyList<IWizardStepViewModel> _allSteps;
    private readonly WizardContext _context;
    private readonly WizardHostingMode _mode;
    private List<IWizardStepViewModel> _activeSteps;
    private int _currentIndex;
    private bool _disposed;

    public WizardOrchestrator(IReadOnlyList<IWizardStepViewModel> steps, WizardContext context)
        : this(steps, context, WizardHostingMode.Init)
    {
    }

    public WizardOrchestrator(
        IReadOnlyList<IWizardStepViewModel> steps,
        WizardContext context,
        WizardHostingMode mode)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(context);

        _allSteps = steps;
        _context = context;
        _mode = mode;
        _activeSteps = BuildInitialActiveSteps();

        if (_activeSteps.Count > 0)
            _activeSteps[0].OnEnter(context, NavigationDirection.Forward);
    }

    /// <summary>
    /// Construct an orchestrator that hosts a single editor with no linear
    /// step-list navigation. Save exits and cancel exits are the host's
    /// responsibility — the orchestrator provides <see cref="WriteConfig"/>
    /// and disposal. Used for the future <c>netclaw config</c> single-step
    /// host and for init-owned re-entry.
    /// </summary>
    /// <remarks>
    /// The orchestrator takes ownership of <paramref name="step"/>'s
    /// lifetime — disposing the orchestrator disposes the step. Hosts MUST
    /// NOT dispose the step independently.
    /// </remarks>
    public static WizardOrchestrator ForSingleStep(
        IWizardStepViewModel step, WizardContext context)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new WizardOrchestrator([step], context, WizardHostingMode.SingleStep);
    }

    /// <summary>Hosting mode. Affects which writers run on save.</summary>
    public WizardHostingMode Mode => _mode;

    /// <summary>True when the orchestrator hosts a single editor for non-linear save/cancel.</summary>
    public bool IsSingleStep => _mode == WizardHostingMode.SingleStep;

    /// <summary>The currently active step, or null if no steps are active.</summary>
    public IWizardStepViewModel? CurrentStep =>
        _currentIndex >= 0 && _currentIndex < _activeSteps.Count
            ? _activeSteps[_currentIndex]
            : null;

    /// <summary>Reactive property that emits the current step index for UI binding.</summary>
    public ReactiveProperty<int> CurrentStepIndex { get; } = new(0);

    /// <summary>Number of active (non-skipped) steps in the wizard.</summary>
    public int ActiveStepCount => _activeSteps.Count;

    /// <summary>
    /// Returns the 1-based display number for the current step,
    /// accounting for skipped steps.
    /// </summary>
    public int GetDisplayStepNumber() => _currentIndex + 1;

    /// <summary>
    /// Returns the 1-based display number for a given step by its ID.
    /// Returns -1 if the step is not in the active list.
    /// </summary>
    public int GetDisplayStepNumber(string stepId)
    {
        for (var i = 0; i < _activeSteps.Count; i++)
        {
            if (_activeSteps[i].StepId == stepId)
                return i + 1;
        }
        return -1;
    }

    /// <summary>
    /// Advance the wizard. First tries to advance within the current step (sub-step).
    /// If the step reports completion, moves to the next applicable step.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the wizard advanced. <c>false</c> when there is no next
    /// step — in <see cref="WizardHostingMode.Init"/> this means the linear
    /// list is exhausted and the host SHOULD show review/save; in
    /// <see cref="WizardHostingMode.SingleStep"/> this means the lone
    /// editor reports completion and the host SHOULD save and exit. Hosts
    /// SHALL branch on <see cref="Mode"/> (or <see cref="IsSingleStep"/>)
    /// to disambiguate.
    /// </returns>
    public bool GoNext()
    {
        var current = CurrentStep;
        if (current is null)
            return false;

        // Let the step handle internal advancement (sub-steps)
        if (current.TryAdvance())
        {
            _context.StatusMessage.Value = "";
            return true;
        }

        // Step is complete — move to the next applicable step.
        // Capture current position before OnLeave/rebuild can shift the index.
        var currentIdx = _currentIndex;
        current.OnLeave();
        _activeSteps = RebuildActiveSteps();

        var nextIndex = currentIdx + 1;
        if (nextIndex >= _activeSteps.Count)
            return false; // already at the end

        _currentIndex = nextIndex;
        CurrentStepIndex.Value = _currentIndex;
        _activeSteps[_currentIndex].OnEnter(_context, NavigationDirection.Forward);
        _context.StatusMessage.Value = "";
        return true;
    }

    /// <summary>
    /// Go back in the wizard. First tries to go back within the current step (sub-step).
    /// If at the first sub-step, moves to the previous applicable step and enters it
    /// with <see cref="NavigationDirection.Back"/> so it resumes at its last sub-step.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the wizard went back. <c>false</c> when the host
    /// SHOULD treat the action as "quit / cancel without save". In both
    /// modes, leaving the active step's <see cref="IWizardStepViewModel.OnLeave"/>
    /// hook fires before this returns so editors can release subscriptions.
    /// </returns>
    public bool GoBack()
    {
        var current = CurrentStep;
        if (current is null)
            return false;

        // Let the step handle internal back-navigation (sub-steps)
        if (current.TryGoBack())
        {
            _context.StatusMessage.Value = "";
            return true;
        }

        // At the first sub-step — move to the previous applicable step (or
        // cancel out of the wizard if there is none).
        var currentIdx = _currentIndex;
        if (currentIdx <= 0)
        {
            // Cancel / quit path — call OnLeave so the editor can release
            // reactive subscriptions or cancel inflight work before the
            // host disposes the orchestrator (or re-hosts the same leaf).
            current.OnLeave();
            return false;
        }

        current.OnLeave();
        _activeSteps = RebuildActiveSteps();

        var prevIndex = currentIdx - 1;
        if (prevIndex < 0)
            return false;

        _currentIndex = prevIndex;
        CurrentStepIndex.Value = _currentIndex;
        _activeSteps[_currentIndex].OnEnter(_context, NavigationDirection.Back);
        _context.StatusMessage.Value = "";
        return true;
    }

    /// <summary>
    /// Collect config contributions from all active steps and write all config
    /// files. Init-only side artifacts (identity files, seeded agents,
    /// provider credentials, bootstrap device) SHALL run only when
    /// <see cref="Mode"/> is <see cref="WizardHostingMode.Init"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Single-step save support REQUIRES the semantic merge layer (section
    /// 5 of the section-editor-abstraction change). Until that lands,
    /// invoking <see cref="WriteConfig"/> in
    /// <see cref="WizardHostingMode.SingleStep"/> mode throws — the
    /// existing <see cref="WizardConfigBuilder.WriteConfigFile"/> path
    /// rewrites <c>netclaw.json</c> from scratch and would silently wipe
    /// unrelated sections.
    /// </para>
    /// </remarks>
    public void WriteConfig()
    {
        if (_mode == WizardHostingMode.SingleStep)
        {
            throw new InvalidOperationException(
                "Single-step WriteConfig requires the semantic merge writer (section-editor-abstraction § 5). " +
                "Calling it now would overwrite netclaw.json with only the lone leaf's contribution and " +
                "discard all unrelated sections. Land § 5 before enabling single-step saves.");
        }

        _context.Paths.EnsureDirectoriesExist();

        var configBuilder = new WizardConfigBuilder(_context.Paths);
        var secretsBuilder = new WizardSecretsBuilder(_context.Paths);

        foreach (var step in _activeSteps)
        {
            step.ContributeConfig(configBuilder);
            step.ContributeSecrets(secretsBuilder);
        }

        configBuilder.WriteConfigFile();
        secretsBuilder.WriteSecretsFile();

        // Init-only side artifacts run only in Init mode. Once § 5 lands
        // and single-step saves are unblocked, the guard above will be
        // removed but this branch stays — the side artifacts are
        // bootstrap-only by design.
        if (_mode != WizardHostingMode.Init)
            return;

        // Write provider credentials (deferred from ContributeSecrets to finalization)
        var providerStep = _activeSteps.OfType<ProviderStepViewModel>().FirstOrDefault();
        providerStep?.WriteProviderCredentials(_context.Paths);

        // Write identity files and seed built-in agents from the identity step
        var identityStep = _activeSteps.OfType<IdentityStepViewModel>().FirstOrDefault();
        if (identityStep is not null)
        {
            identityStep.WriteIdentityFiles(_context.Paths);
            identityStep.SeedBuiltInAgents(_context.Paths);
        }

        // Write bootstrap paired device for non-Local exposure modes so the daemon
        // can start with at least one paired device (satisfies ExposureModeValidationService).
        var exposureStep = _activeSteps.OfType<ExposureModeStepViewModel>().FirstOrDefault();
        exposureStep?.WriteBootstrapDevice(_context.Paths);
    }

    /// <summary>
    /// Run health checks from all steps.
    /// </summary>
    public async Task RunHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        foreach (var step in _activeSteps)
        {
            ct.ThrowIfCancellationRequested();
            await step.ContributeHealthChecksAsync(runner, ct);
        }
    }

    /// <summary>
    /// Build the initial active step list (called from constructor before _activeSteps is assigned).
    /// </summary>
    private List<IWizardStepViewModel> BuildInitialActiveSteps()
    {
        _currentIndex = 0;
        return [.. _allSteps.Where(s => s.IsApplicable(_context))];
    }

    /// <summary>
    /// Re-evaluate which steps are applicable based on current context.
    /// Preserves the current step's position in the list.
    /// </summary>
    private List<IWizardStepViewModel> RebuildActiveSteps()
    {
        var currentStepId = CurrentStep?.StepId;
        var active = _allSteps.Where(s => s.IsApplicable(_context)).ToList();

        // Try to preserve the current index pointing at the same step
        if (currentStepId is not null)
        {
            for (var i = 0; i < active.Count; i++)
            {
                if (active[i].StepId == currentStepId)
                {
                    _currentIndex = i;
                    return active;
                }
            }
        }

        // Current step was removed from active list — clamp index
        if (_currentIndex >= active.Count)
            _currentIndex = Math.Max(0, active.Count - 1);

        return active;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        CurrentStepIndex.Dispose();
        foreach (var step in _allSteps)
            step.Dispose();
    }
}
