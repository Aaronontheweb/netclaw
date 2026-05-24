// -----------------------------------------------------------------------
// <copyright file="ConfigDashboardPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Sections;
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
/// and dispatches selections back to the ViewModel, which in turn drives
/// Termina's in-process router for the routed handoffs.
/// </summary>
public sealed class ConfigDashboardPage : ReactivePage<ConfigDashboardViewModel>
{
    private DynamicLayoutNode? _contentNode;
    private SelectionListNode<string>? _selectionList;
    private readonly CompositeDisposable _stepSubs = [];

    protected override void OnBound()
    {
        base.OnBound();

        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        // Re-render whenever the operator enters or leaves a Domain sub-menu.
        ViewModel.ActiveDomain
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
        foreach (var leafId in domain.LeafIds)
        {
            var leaf = ViewModel.Registry.Find(leafId);
            leafLabels.Add(leaf is null
                ? $"  {leafId}  (not registered in this build)"
                : $"  {leaf.DisplayName}  —  {leaf.Summary(context)}");
        }
        leafLabels.Add("  ← Back to root");

        _selectionList = Layouts.SelectionList(leafLabels)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);
        _selectionList.OnFocused();

        _selectionList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0) return;
                if (selected[0].Contains("Back to root", StringComparison.Ordinal))
                {
                    ViewModel.GoBackToRoot();
                }
                // Leaf-level editing is dispatched through a single-step
                // host in a follow-up commit; selecting a leaf here is a
                // no-op for now beyond highlighting it. The selection
                // remains visible so the operator can see the leaf's
                // current state via its Summary line.
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  {domain.DisplayName}").WithForeground(Color.White).Bold())
            .WithChild(new TextNode($"  {domain.Description}").WithForeground(Color.Gray))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(_selectionList);
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
        // The dashboard renders summaries from on-disk config without
        // touching secrets — leaves report presence via the probe.
        var paths = new Configuration.NetclawPaths();
        var dict = File.Exists(paths.NetclawConfigPath)
            ? Netclaw.Cli.Config.ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath)
            : new Dictionary<string, object>();
        return new SectionEditorContext(
            paths: paths,
            config: dict,
            secretPresent: p => Netclaw.Cli.Config.ConfigFileHelper.SecretPresent(paths, p));
    }

    public override void Dispose()
    {
        _stepSubs.Dispose();
        base.Dispose();
    }
}
