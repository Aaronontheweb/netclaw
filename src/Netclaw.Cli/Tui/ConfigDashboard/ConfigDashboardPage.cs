// -----------------------------------------------------------------------
// <copyright file="ConfigDashboardPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Sections;
using Netclaw.Configuration;
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.ConfigDashboard;

/// <summary>
/// Termina page for the post-install <c>netclaw config</c> dashboard.
/// Renders the root domain-oriented menu from <see cref="ConfigDashboardViewModel"/>
/// and dispatches selections back to the ViewModel. Routed handoffs shut
/// down cleanly and Program.cs surfaces the follow-up command.
/// </summary>
public sealed class ConfigDashboardPage : ReactivePage<ConfigDashboardViewModel>
{
    private DynamicLayoutNode? _contentNode;
    private SelectionListNode<string>? _selectionList;
    private readonly CompositeDisposable _stepSubs = [];
    private readonly NetclawPaths _paths;
    private readonly ReactiveProperty<string> _leafSelectionHint = new(string.Empty);

    public ConfigDashboardPage(NetclawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    protected override void OnBound()
    {
        base.OnBound();

        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        // Re-render whenever the operator enters or leaves a Domain sub-menu.
        ViewModel.ActiveDomain
            .Subscribe(_ =>
            {
                _leafSelectionHint.Value = string.Empty;
                _contentNode?.Invalidate();
            })
            .DisposeWith(Subscriptions);

        // Re-render whenever the leaf-selection hint changes so the
        // operator sees the "editor not yet wired" message immediately.
        _leafSelectionHint
            .Subscribe(_ => _contentNode?.Invalidate())
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            .WithChild(
                new PanelNode()
                    .WithTitle("Netclaw configuration")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(BuildInnerLayout())
                    .Fill());
    }

    private ILayoutNode BuildInnerLayout()
    {
        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildKeyBindings());
    }

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            _stepSubs.Clear();
            return ViewModel.ActiveDomain.Value is null
                ? BuildRootMenu()
                : BuildDomainSubMenu(ViewModel.ActiveDomain.Value);
        });
        return _contentNode;
    }

    private ILayoutNode BuildRootMenu()
    {
        var labels = new List<string>();
        for (var i = 0; i < ViewModel.RootEntries.Count; i++)
        {
            var e = ViewModel.RootEntries[i];
            var marker = e.Kind == ConfigDashboardEntryKind.Routed ? "  ↗" : "  ▸";
            labels.Add($"{marker} {e.DisplayName}");
        }

        // Append affordances at the bottom so the operator can select
        // them with the same selection-list controls as the entries.
        var affordanceStart = labels.Count;
        foreach (var a in ViewModel.Affordances)
            labels.Add($"  • {a.DisplayName}");

        _selectionList = Layouts.SelectionList(labels)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);
        _selectionList.OnFocused();

        _selectionList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0) return;
                var idx = labels.IndexOf(selected[0]);
                if (idx < 0) return;

                if (idx < ViewModel.RootEntries.Count)
                    ViewModel.ActivateEntry(ViewModel.RootEntries[idx]);
                else
                    ViewModel.ActivateAffordance(ViewModel.Affordances[idx - affordanceStart]);
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Select an area:").WithForeground(Color.White).Bold())
            .WithChild(_selectionList);
    }

    private ILayoutNode BuildDomainSubMenu(ConfigDashboardEntry domain)
    {
        var context = BuildReadOnlyContext();

        var leafLabels = new List<string>();
        var leafIdByLabel = new Dictionary<string, string>();
        foreach (var leafId in domain.LeafIds)
        {
            var leaf = ViewModel.Registry.Find(leafId);
            var label = leaf is null
                ? $"  {leafId}  (not registered in this build)"
                : $"  {leaf.DisplayName}  —  {leaf.Summary(context)}";
            leafLabels.Add(label);
            leafIdByLabel[label] = leafId;
        }
        const string BackLabel = "  ← Back to root";
        leafLabels.Add(BackLabel);

        _selectionList = Layouts.SelectionList(leafLabels)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);
        _selectionList.OnFocused();

        _selectionList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0) return;
                if (selected[0] == BackLabel)
                {
                    ViewModel.GoBackToRoot();
                    return;
                }
                // Leaf-level editing requires a Termina SingleStepPage that
                // hosts one IWizardStepViewModel. Until that lands, show
                // the operator an explicit "not yet wired" message rather
                // than silently swallowing Enter. Fail loud over silent.
                if (leafIdByLabel.TryGetValue(selected[0], out var leafId))
                {
                    _leafSelectionHint.Value =
                        $"  Leaf editor for '{leafId}' is not yet wired. " +
                        $"Use `netclaw init` or the related per-domain CLI command for now.";
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  {domain.DisplayName}").WithForeground(Color.White).Bold())
            .WithChild(new TextNode($"  {domain.Description}").WithForeground(Color.Gray))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(_selectionList)
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(new TextNode(_leafSelectionHint.Value).WithForeground(Color.Yellow));
    }

    private ILayoutNode BuildKeyBindings()
    {
        return ViewModel.ActiveDomain.Value is null
            ? new TextNode("  [↑/↓] Navigate  [Enter] Select  [Esc] Quit").WithForeground(Color.Gray)
            : new TextNode("  [↑/↓] Navigate  [Enter] Select  [Esc] Back").WithForeground(Color.Gray);
    }

    private void HandleKeyPress(KeyPressed key)
    {
        if (key.KeyInfo.Key == ConsoleKey.Escape)
        {
            if (ViewModel.ActiveDomain.Value is not null)
                ViewModel.GoBackToRoot();
            else
                ViewModel.ActivateAffordance(ViewModel.Affordances.First(a => a.Id == "quit"));
        }
    }

    private SectionEditorContext BuildReadOnlyContext()
    {
        // Use the DI-injected NetclawPaths so the dashboard honors
        // NETCLAW_HOME — a `new NetclawPaths()` would silently fall back
        // to the default and render wrong summaries for any operator who
        // configured a custom install path. CLAUDE.md "no silent fallbacks."
        var dict = File.Exists(_paths.NetclawConfigPath)
            ? Netclaw.Cli.Config.ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath)
            : new Dictionary<string, object>();
        return new SectionEditorContext(
            paths: _paths,
            config: dict,
            secretPresent: p => Netclaw.Cli.Config.ConfigFileHelper.SecretPresent(_paths, p));
    }

    public override void Dispose()
    {
        _stepSubs.Dispose();
        _leafSelectionHint.Dispose();
        base.Dispose();
    }
}
