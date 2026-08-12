// -----------------------------------------------------------------------
// <copyright file="InlineChatPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Netclaw.Actors.Protocol;
using R3;
using Termina.Clipboard;
using Termina.Components.Streaming;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Primary-buffer chat page. Stable blocks enter terminal scrollback.
/// The live region contains activity, approvals, the composer, and status.
/// </summary>
public sealed class InlineChatPage : ReactivePage<ChatViewModel>
{
    private static readonly TimeSpan DoubleEscapeWindow = TimeSpan.FromMilliseconds(500);
    private const int MaximumReadableWidth = 120;

    private readonly IAnsiTerminal _terminal;
    private readonly IInlineOutput _inlineOutput;
    private readonly IClipboardService _clipboardService;
    private readonly TimeProvider _timeProvider;
    private readonly object _commitLock = new();
    private readonly CompositeDisposable _approvalSubscriptions = [];

    private TextAreaNode _promptInput = null!;
    private DynamicLayoutNode _liveRegion = null!;
    private SelectionListNode<string>? _approvalList;
    private ScrollableContainerNode? _approvalDetail;
    private CopyableTextNode? _inspectorCopyNode;
    private ScrollableContainerNode? _inspectorDetail;
    private string? _approvalCallId;
    private string? _approvalDetailCallId;
    private string? _inspectorBlockKey;
    private string? _inspectorCopyStatus;
    private int _inspectorRenderWidth;
    private ChatPresentationState _state = ChatPresentationState.Empty;
    private Task _commitTail = Task.CompletedTask;
    private readonly List<ChatPresentationBlock> _deferredInspectorCommits = [];
    private long? _lastEscapeTimestamp;
    private int _inspectorIndex;
    private bool _inspectorOpen;

    public InlineChatPage(
        IAnsiTerminal terminal,
        IInlineOutput inlineOutput,
        IClipboardService clipboardService,
        TimeProvider timeProvider)
    {
        _terminal = terminal;
        _inlineOutput = inlineOutput;
        _clipboardService = clipboardService;
        _timeProvider = timeProvider;
        FocusPolicy = FocusPolicy.FirstFocusable;
    }

    protected override void OnBound()
    {
        base.OnBound();

        _promptInput = new TextAreaNode()
            .WithPlaceholder("Ask Netclaw...")
            .WithMaxHeight(8)
            .WithHistory(100)
            .WithNewlineModifier(ConsoleModifiers.Shift);
        _liveRegion = new DynamicLayoutNode(BuildLiveRegion);

        _promptInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(SubmitPrompt)
            .DisposeWith(Subscriptions);

        ViewModel.SessionOutput
            .Subscribe(output => Post(() => ApplyOutput(output)))
            .DisposeWith(Subscriptions);

        ViewModel.StatusMessage
            .Subscribe(_ => _liveRegion.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.SessionIdDisplay
            .Subscribe(_ => _liveRegion.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.IsApprovalDetailVisible
            .Subscribe(_ => _liveRegion.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.IsGenerating
            .Subscribe(isGenerating =>
            {
                if (isGenerating)
                    Focus.ClearFocus();
                else if (ShowsComposer(_state))
                    Focus.SetFocus(_promptInput);
                _liveRegion.Invalidate();
            })
            .DisposeWith(Subscriptions);
        ViewModel.Input.OfType<IInputEvent, ResizeEvent>()
            .Subscribe(_ => _liveRegion.Invalidate())
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout() => _liveRegion;

    internal int ApprovalDetailScrollOffset => _approvalDetail?.ScrollOffset ?? 0;

    internal bool ApprovalDetailCanScrollDown => _approvalDetail?.CanScrollDown == true;

    public override bool HandlePageInput(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Q
            && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestAppShutdown();
            return true;
        }

        if (_inspectorOpen)
            return HandleInspectorInput(keyInfo);

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            HandleEscape();
            return true;
        }

        if (_state.PendingApproval is not null && ViewModel.IsApprovalDetailVisible.Value)
        {
            if (keyInfo.Key == ConsoleKey.PageUp)
            {
                _approvalDetail?.PageUp();
                return true;
            }

            if (keyInfo.Key == ConsoleKey.PageDown)
            {
                _approvalDetail?.PageDown();
                return true;
            }
        }

        if (_state.PendingApproval is not null
            && keyInfo.Key == ConsoleKey.O
            && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.ToggleApprovalDetail();
            return true;
        }

        if (_state.PendingApproval is null
            && keyInfo.Key == ConsoleKey.O
            && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control)
            && _state.Transcript.Count > 0)
        {
            OpenInspector();
            return true;
        }

        return base.HandlePageInput(keyInfo);
    }

    private void SubmitPrompt(string text)
    {
        _promptInput.Clear();
        _lastEscapeTimestamp = null;
        ApplyReduction(ChatPresentationReducer.RecordUserPrompt(
            _state,
            text,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
        _ = ViewModel.SubmitAsync(text);
    }

    private void ApplyOutput(SessionOutput output)
    {
        ApplyReduction(ChatPresentationReducer.Reduce(_state, output));
    }

    private void ApplyReduction(ChatReduction reduction)
    {
        var hadComposer = ShowsComposer(_state);
        var hadApproval = _state.PendingApproval is not null;
        _state = reduction.State;

        foreach (var effect in reduction.Effects)
        {
            switch (effect)
            {
                case ChatPresentationEffect.Commit commit:
                    if (_inspectorOpen)
                        _deferredInspectorCommits.Add(commit.Block);
                    else
                        QueueCommit(commit.Block);
                    break;
                case ChatPresentationEffect.SetStatus status:
                    ViewModel.StatusMessage.Value = status.Text;
                    break;
            }
        }

        var hasApproval = _state.PendingApproval is not null;
        var hasComposer = ShowsComposer(_state);
        if (hadApproval != hasApproval || hadComposer != hasComposer)
        {
            if (!hasApproval)
                ClearApprovalList();
            InvalidateLayout();
            if (hasApproval)
                Focus.SetFocus(EnsureApprovalList());
            else if (hasComposer)
                Focus.SetFocus(_promptInput);
            else
                Focus.ClearFocus();
        }
        else
        {
            _liveRegion.Invalidate();
        }
    }

    private void QueueCommit(ChatPresentationBlock block)
    {
        lock (_commitLock)
        {
            _commitTail = CommitAfterAsync(_commitTail, block);
        }
    }

    private async Task CommitAfterAsync(Task prior, ChatPresentationBlock block)
    {
        try
        {
            await prior.ConfigureAwait(false);
            await _inlineOutput.CommitAsync(
                ChatPresentationRenderer.BuildStableBlock(block, _terminal.Width),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                ViewModel.StatusMessage.Value = $"Output failed: {ex.Message}";
                _liveRegion.Invalidate();
            });
        }
    }

    private ILayoutNode BuildLiveRegion()
    {
        if (_inspectorOpen)
            return BuildInspector();

        var content = Layouts.Vertical()
            .WithChild(BuildSessionHeader())
            .WithChild(BuildActivityDeck());

        if (_state.PendingApproval is not null)
            content.WithChild(BuildDecisionGate(_state.PendingApproval));
        else if (ShowsComposer(_state))
            content.WithChild(BuildComposer());

        return content.WithChild(BuildStatusLine());
    }

    private ILayoutNode BuildInspector()
    {
        var block = _state.Transcript[_inspectorIndex];
        var detailWidth = Math.Max(1, ReadableWidth() - 4);
        if (_inspectorDetail is null
            || _inspectorBlockKey != block.Key
            || _inspectorRenderWidth != detailWidth)
        {
            _inspectorBlockKey = block.Key;
            _inspectorRenderWidth = detailWidth;
            _inspectorDetail ??= new ScrollableContainerNode()
                .WithAutoScroll(AutoScrollPolicy.None)
                .WithScrollbar(true);
            var semanticText = ChatPresentationRenderer.SemanticCopyText(block.SemanticText);
            var displayText = RemoveDuplicateInspectorLabel(semanticText, block.Label);
            if (block.Kind == ChatBlockKind.Assistant)
            {
                displayText = ChatPresentationRenderer.MarkdownToPlainText(displayText);
                displayText = WordWrapInspectorText(displayText, detailWidth);
            }
            _inspectorCopyNode = new CopyableTextNode(_clipboardService, displayText)
                .WithSemanticContent(semanticText)
                .WithHint(null);
            _inspectorDetail.WithContent(_inspectorCopyNode);
            _inspectorDetail.ScrollToTop();
        }

        var detailLabel = block.Kind == ChatBlockKind.Tool ? "  RAW DETAIL" : string.Empty;
        var heading = $" INSPECTOR  {_inspectorIndex + 1}/{_state.Transcript.Count}  {block.Label}{detailLabel}";
        var content = Layouts.Vertical()
            .WithChild(new TextNode(heading).WithForeground(Color.BrightBlue).Bold())
            .WithChild(_inspectorDetail.Fill());
        if (_inspectorCopyStatus is not null)
        {
            var color = _inspectorCopyStatus.StartsWith("Copy failed", StringComparison.Ordinal)
                ? Color.Red
                : Color.Green;
            content.WithChild(new TextNode($" {_inspectorCopyStatus}").WithForeground(color));
        }
        var help = _terminal.Width >= 86
            ? " ↑↓ event · PgUp/PgDn detail · Y event · Shift+Y turn · Ctrl+O/Esc close"
            : " ↑↓ event · Pg scroll · Y event · Shift+Y turn · Esc close";
        content.WithChild(new TextNode(help)
            .WithForeground(Color.Gray));

        var panel = new PanelNode()
            .WithBorder(BorderStyle.Single)
            .WithBorderColor(Color.BrightBlue)
            .WithContent(content)
            .Width(ReadableWidth())
            .Height(Math.Max(8, _terminal.Height - 4));

        var inspector = Layouts.Vertical()
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(panel);
        return Layouts.Horizontal()
            .WithChild(inspector)
            .WithChild(Layouts.Empty().Fill());
    }

    private static string RemoveDuplicateInspectorLabel(string text, string label)
    {
        var prefix = $"{label}\n";
        return text.StartsWith(prefix, StringComparison.Ordinal)
            ? text[prefix.Length..]
            : text;
    }

    private static string WordWrapInspectorText(string text, int width) => string.Join(
        '\n',
        WordWrapper.WrapLines(text.ReplaceLineEndings("\n").Split('\n'), width));

    private bool HandleInspectorInput(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.Escape:
                CloseInspector();
                return true;
            case ConsoleKey.O when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control):
                CloseInspector();
                return true;
            case ConsoleKey.UpArrow:
                SelectInspectorEvent(-1);
                return true;
            case ConsoleKey.DownArrow:
                SelectInspectorEvent(1);
                return true;
            case ConsoleKey.PageUp:
                _inspectorDetail?.PageUp();
                return true;
            case ConsoleKey.PageDown:
                _inspectorDetail?.PageDown();
                return true;
            case ConsoleKey.Home:
                _inspectorDetail?.ScrollToTop();
                return true;
            case ConsoleKey.End:
                _inspectorDetail?.ScrollToBottom();
                return true;
            case ConsoleKey.Y:
                CopyInspectorSelection(keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift));
                return true;
            default:
                return true;
        }
    }

    private void OpenInspector()
    {
        _inspectorOpen = true;
        _inspectorIndex = FindDefaultInspectorIndex();
        _inspectorBlockKey = null;
        _inspectorCopyStatus = null;
        _lastEscapeTimestamp = null;
        Focus.ClearFocus();
        InvalidateLayout();
    }

    private void CloseInspector()
    {
        _inspectorOpen = false;
        _inspectorBlockKey = null;
        _inspectorCopyStatus = null;
        InvalidateLayout();
        if (_state.PendingApproval is not null)
            Focus.SetFocus(EnsureApprovalList());
        else if (ShowsComposer(_state))
            Focus.SetFocus(_promptInput);
        else
            Focus.ClearFocus();

        foreach (var block in _deferredInspectorCommits)
            QueueCommit(block);
        _deferredInspectorCommits.Clear();
    }

    private void SelectInspectorEvent(int delta)
    {
        var index = Math.Clamp(_inspectorIndex + delta, 0, _state.Transcript.Count - 1);
        if (index == _inspectorIndex)
            return;

        _inspectorIndex = index;
        _inspectorBlockKey = null;
        _inspectorCopyStatus = null;
        _liveRegion.Invalidate();
    }

    private void CopyInspectorSelection(bool completeTurn)
    {
        if (_inspectorCopyNode is null)
            return;

        var semanticText = completeTurn
            ? ChatPresentationRenderer.BuildSemanticTurn(_state.Transcript, _inspectorIndex)
            : ChatPresentationRenderer.SemanticCopyText(_state.Transcript[_inspectorIndex].SemanticText);
        _inspectorCopyNode.WithSemanticContent(semanticText);
        var success = _inspectorCopyNode.TryCopy();
        _inspectorCopyStatus = success
            ? completeTurn ? "Turn copied" : "Event copied"
            : "Copy failed. The selected event remains available.";
        _liveRegion.Invalidate();
    }

    private ILayoutNode BuildSessionHeader()
    {
        var session = ViewModel.SessionIdDisplay.Value;
        var connectionCue = _state.HasJoined ? "●" : "○";
        var sessionPart = string.IsNullOrWhiteSpace(session)
            ? "connecting"
            : $"session {_state.SessionTitle ?? ChatPresentationRenderer.CompactIdentity(session, 30)}";
        var modelPart = _terminal.Width >= 100 ? $"  model {ViewModel.ModelId}" : string.Empty;
        var contextPart = _terminal.Width >= 110 && _state.ContextUsagePercent is { } usage
            ? $"  context {Math.Round(usage * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%"
            : string.Empty;
        var daemonPart = _terminal.Width >= 140
            ? _state.HasJoined ? "  daemon connected" : "  daemon connecting"
            : string.Empty;
        return new TextNode($" netclaw  {connectionCue} {sessionPart}{modelPart}{contextPart}{daemonPart}")
            .WithForeground(Color.BrightBlue)
            .Bold();
    }

    private ILayoutNode BuildActivityDeck()
    {
        var rows = new List<ILayoutNode>();
        var lineWidth = Math.Max(20, ReadableWidth() - 2);
        if (!string.IsNullOrWhiteSpace(_state.ThoughtText))
        {
            rows.Add(new TextNode(ChatPresentationRenderer.OneLine(
                    $" ◌ THOUGHT  {_state.ThoughtText}",
                    lineWidth))
                .WithForeground(Color.Yellow));
        }

        var agents = _state.SubAgents.Values
            .OrderBy(value => value.StartedAtMs)
            .ThenBy(value => value.RunId, StringComparer.Ordinal)
            .ToList();
        var renderedAgents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in _state.Tools.Values
                     .OrderBy(value => value.StartedAtMs)
                     .ThenBy(value => value.CallId, StringComparer.Ordinal))
        {
            var childRuns = agents.Where(value => value.ParentCallId == tool.CallId).ToList();
            var phase = childRuns.Count switch
            {
                0 => tool.Phase,
                1 => $"orchestrating {childRuns[0].AgentName}",
                _ => $"orchestrating {childRuns.Count} agents"
            };
            var summary = string.IsNullOrWhiteSpace(tool.Summary) ? string.Empty : $"  {tool.Summary}";
            rows.Add(new TextNode(ChatPresentationRenderer.OneLine(
                    $" ◌ TOOL  {tool.ToolName}  {phase}{summary}",
                    lineWidth))
                .WithForeground(ActivityColor(tool.Phase)));

            foreach (var run in childRuns)
            {
                rows.Add(BuildAgentActivity(run, lineWidth));
                renderedAgents.Add(run.RunId);
            }
        }

        foreach (var run in agents.Where(value => !renderedAgents.Contains(value.RunId)))
            rows.Add(BuildAgentActivity(run, lineWidth));

        if (rows.Count == 0 && _state.IsProcessing)
            rows.Add(new TextNode(" ◌ WORKING").WithForeground(Color.Yellow));

        return rows.Count == 0 ? Layouts.Empty() : Layouts.Vertical([.. rows]);
    }

    private ILayoutNode BuildComposer()
    {
        var composer = new PanelNode()
            .WithTitle("Composer")
            .WithBorder(BorderStyle.Rounded)
            .WithBorderColor(Color.Cyan)
            .WithContent(_promptInput)
            .Width(ReadableWidth())
            .HeightAuto(min: 3, max: Math.Max(3, Math.Min(10, _terminal.Height / 3)));
        return Layouts.Horizontal()
            .WithChild(composer)
            .WithChild(Layouts.Empty().Fill());
    }

    private ILayoutNode BuildDecisionGate(ToolInteractionRequest approval)
    {
        var width = Math.Max(20, ReadableWidth() - 4);
        var detailHeight = ApprovalDetailHeight(approval, width);
        var maximumHeight = ViewModel.IsApprovalDetailVisible.Value
            ? detailHeight + approval.Options.Count + 4
            : approval.Options.Count + 5;
        var gate = Layouts.Vertical()
            .WithChild(new TextNode($" APPROVAL  {ChatPresentationRenderer.ApprovalPath(_state, approval)}")
                .WithForeground(Color.Yellow)
                .Bold());
        if (ViewModel.IsApprovalDetailVisible.Value)
        {
            gate.WithChild(EnsureApprovalDetail(approval)
                .Height(detailHeight));
        }
        else
        {
            gate.WithChild(new TextNode(ChatPresentationRenderer.OneLine(
                    approval.DisplayText,
                    Math.Max(20, width - 4)))
                .WithForeground(Color.White));
        }

        gate
            .WithChild(new TextNode(
                    ViewModel.IsApprovalDetailVisible.Value
                        ? " PgUp/PgDn scroll · Ctrl+O close details · Escape deny"
                        : " Ctrl+O details · Escape deny")
                .WithForeground(Color.Gray))
            .WithChild(EnsureApprovalList());

        var panel = new PanelNode()
            .WithTitle("Decision Gate")
            .WithBorder(BorderStyle.Rounded)
            .WithBorderColor(Color.Yellow)
            .WithContent(gate)
            .Width(ReadableWidth())
            .HeightAuto(min: 6, max: Math.Max(6, Math.Min(maximumHeight, _terminal.Height / 2)));
        return Layouts.Horizontal()
            .WithChild(panel)
            .WithChild(Layouts.Empty().Fill());
    }

    private SelectionListNode<string> EnsureApprovalList()
    {
        var approval = _state.PendingApproval
            ?? throw new InvalidOperationException("An approval list requires a pending approval.");
        if (_approvalList is not null && _approvalCallId == approval.CallId.Value)
            return _approvalList;

        ClearApprovalList();
        _approvalCallId = approval.CallId.Value;
        _approvalList = Layouts.SelectionList(approval.Options.Select(ApprovalOptionLabel).ToList())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Yellow);
        _approvalList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var option = approval.Options.FirstOrDefault(candidate =>
                        string.Equals(ApprovalOptionLabel(candidate), selected[0], StringComparison.Ordinal));
                    if (option is not null)
                        _ = ViewModel.SubmitInteractionOptionAsync(option.Label);
                }
            })
            .DisposeWith(_approvalSubscriptions);
        return _approvalList;
    }

    private void ClearApprovalList()
    {
        _approvalSubscriptions.Clear();
        _approvalList = null;
        _approvalCallId = null;
        _approvalDetail = null;
        _approvalDetailCallId = null;
    }

    private static string ApprovalOptionLabel(ToolInteractionOption option) => option.Key.Value switch
    {
        ApprovalOptionKeys.ApproveOnce => $"{option.Label} — only this request",
        ApprovalOptionKeys.ApproveSession => $"{option.Label} — until this chat ends",
        ApprovalOptionKeys.ApproveAlways => $"{option.Label} — this tool in this folder",
        ApprovalOptionKeys.ApproveEverywhere => $"{option.Label} — this tool in any folder",
        ApprovalOptionKeys.Deny => $"{option.Label} — do not run",
        _ => option.Label
    };

    private ScrollableContainerNode EnsureApprovalDetail(ToolInteractionRequest approval)
    {
        if (_approvalDetail is not null && _approvalDetailCallId == approval.CallId.Value)
            return _approvalDetail;

        _approvalDetailCallId = approval.CallId.Value;
        _approvalDetail = new ScrollableContainerNode()
            .WithAutoScroll(AutoScrollPolicy.None)
            .WithScrollbar(true)
            .WithContent(new TextNode(BuildApprovalDetail(approval)).WithForeground(Color.White));
        return _approvalDetail;
    }

    private ILayoutNode BuildStatusLine()
    {
        var status = ViewModel.StatusMessage.Value;
        var displayStatus = status == "Generating..." ? "Working…" : status;
        var keys = StatusKeys(_terminal.Width, _state.PendingApproval is not null, ShowsComposer(_state));
        var statusLine = Layouts.Horizontal()
            .WithChild(new TextNode($" {displayStatus}")
                .WithForeground(StatusColor(status))
                .WidthAuto())
            .WithChild(new TextNode($" · {keys}")
                .WithForeground(Color.Gray)
                .NoWrap()
                .Fill())
            .Width(ReadableWidth())
            .Height(1);
        return Layouts.Horizontal()
            .WithChild(statusLine)
            .WithChild(Layouts.Empty().Fill());
    }

    private int ReadableWidth() => Math.Min(_terminal.Width, MaximumReadableWidth);

    private static ILayoutNode BuildAgentActivity(SubAgentActivityPresentation run, int lineWidth)
    {
        var summary = string.IsNullOrWhiteSpace(run.Summary) ? string.Empty : $"  {run.Summary}";
        var prefix = run.ParentCallId is null ? " ↳" : "   ↳";
        var rows = new List<ILayoutNode>
        {
            new TextNode(ChatPresentationRenderer.OneLine(
                    $"{prefix} AGENT  {run.AgentName}  {run.Phase}{summary}",
                    lineWidth))
                .WithForeground(ActivityColor(run.Phase))
        };
        if (run.ActiveToolName is not null)
        {
            var toolPrefix = run.ParentCallId is null ? "   ↳" : "     ↳";
            rows.Add(new TextNode(ChatPresentationRenderer.OneLine(
                    $"{toolPrefix} TOOL  {run.ActiveToolName}  {run.Phase}",
                    lineWidth))
                .WithForeground(ActivityColor(run.Phase)));
        }

        return Layouts.Vertical([.. rows]);
    }

    private static string BuildApprovalDetail(ToolInteractionRequest approval)
    {
        var lines = new List<string> { approval.DisplayText };
        if (approval.Patterns.Count > 0)
            lines.Add($"Patterns: {string.Join(", ", approval.Patterns)}");
        if (approval.CandidateVerbs.Count > 0)
            lines.Add($"Verbs: {string.Join(", ", approval.CandidateVerbs)}");
        if (!string.IsNullOrWhiteSpace(approval.Cwd))
            lines.Add($"Directory: {approval.Cwd}");
        if (approval.IsMessy)
            lines.Add("Complex command: persistent approval is unavailable.");
        if (approval.HasAdoptedContext)
        {
            var source = approval.HasThirdPartyAdoptedContext ? "third-party context" : "adopted context";
            lines.Add($"Context: {source}; persisted={approval.PersistedAdoptedContext}.");
        }

        return ChatPresentationRenderer.VisibleControlText(
            string.Join('\n', lines),
            ChatViewModel.MaxExpandedApprovalBodyChars);
    }

    private static int ApprovalDetailHeight(ToolInteractionRequest approval, int width)
    {
        var contentWidth = Math.Max(1, width - 2);
        var lineCount = BuildApprovalDetail(approval)
            .Split('\n')
            .Sum(line => Math.Max(1, (line.Length + contentWidth - 1) / contentWidth));
        return Math.Clamp(lineCount, 3, 10);
    }

    private static string StatusKeys(int width, bool hasApproval, bool hasComposer)
    {
        if (hasApproval)
            return width >= 88
                ? "↑↓ select · Enter confirm · Ctrl+O details · Esc deny · Ctrl+Q quit"
                : "↑↓ select · Enter confirm · Esc deny";
        if (!hasComposer)
            return width >= 70 ? "Ctrl+O inspect · Ctrl+Q quit" : "Ctrl+Q quit";
        if (width >= 110)
            return "Enter send · Shift+Enter newline · Esc Esc clear · Ctrl+O inspect · Ctrl+Q quit";
        return width >= 66
            ? "Enter send · Shift+Enter line · Esc Esc clear · Ctrl+O inspect"
            : "Enter send · Shift+Enter line · Esc Esc clear";
    }

    private static Color ActivityColor(string phase) => phase.ToLowerInvariant() switch
    {
        "queued" => Color.Gray,
        "failed" or "error" or "denied" => Color.Red,
        "completed" or "complete" => Color.Green,
        _ => Color.Yellow
    };

    private int FindDefaultInspectorIndex()
    {
        for (var index = _state.Transcript.Count - 1; index >= 0; index--)
        {
            if (_state.Transcript[index].Kind is not ChatBlockKind.Usage and not ChatBlockKind.System)
                return index;
        }

        return _state.Transcript.Count - 1;
    }

    private void HandleEscape()
    {
        if (_state.PendingApproval is not null)
        {
            _lastEscapeTimestamp = null;
            _ = ViewModel.DenyPendingInteractionAsync();
            return;
        }

        if (ViewModel.IsGenerating.Value)
        {
            _lastEscapeTimestamp = null;
            ViewModel.StatusMessage.Value = "Cancel generation is not supported yet.";
            _liveRegion.Invalidate();
            return;
        }

        var now = _timeProvider.GetTimestamp();
        if (_lastEscapeTimestamp is { } prior
            && _timeProvider.GetElapsedTime(prior, now) <= DoubleEscapeWindow)
        {
            _promptInput.Clear();
            _lastEscapeTimestamp = null;
            ViewModel.StatusMessage.Value = "Input cleared";
            _liveRegion.Invalidate();
            return;
        }

        _lastEscapeTimestamp = now;
    }

    private static Color StatusColor(string status) => status switch
    {
        "Ready" => Color.Green,
        "Approval required" => Color.Yellow,
        _ when status.StartsWith("Connected", StringComparison.Ordinal) => Color.Green,
        _ when status.StartsWith("Reconnected", StringComparison.Ordinal) => Color.Green,
        _ when status.StartsWith("Generating", StringComparison.Ordinal) => Color.Yellow,
        _ when status.StartsWith("Output failed", StringComparison.Ordinal) => Color.Red,
        _ when status.StartsWith("Connection failed", StringComparison.Ordinal) => Color.Red,
        _ => Color.Gray
    };

    private bool ShowsComposer(ChatPresentationState state) =>
        state.PendingApproval is null
        && !ViewModel.IsGenerating.Value
        && !state.IsProcessing
        && state.Tools.Count == 0
        && state.SubAgents.Count == 0
        && string.IsNullOrWhiteSpace(state.ThoughtText)
        && string.IsNullOrWhiteSpace(state.AssistantText);
}

internal static partial class ChatPresentationRenderer
{
    public static ILayoutNode BuildStableBlock(ChatPresentationBlock block, int width)
    {
        var timestamp = block.TimestampMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(block.TimestampMs).ToString("HH:mm")
            : string.Empty;
        var timePart = string.IsNullOrEmpty(timestamp) ? string.Empty : $"  {timestamp}";
        var body = block.Kind == ChatBlockKind.Assistant
            ? MarkdownToPlainText(block.Summary)
            : block.Summary;

        var bodyNode = new TextNode(VisibleControlText(body, 16_000))
            .WithForeground(BodyColor(block))
            .Width(Math.Min(width, MaximumReadableWidth));
        var bodyRow = Layouts.Horizontal()
            .WithChild(bodyNode)
            .WithChild(Layouts.Empty().Fill());

        return Layouts.Vertical()
            .WithChild(new TextNode($"{block.Label}{timePart}")
                .WithForeground(LabelColor(block))
                .Bold())
            .WithChild(bodyRow)
            .WithChild(new TextNode(string.Empty));
    }

    private const int MaximumReadableWidth = 120;

    public static string OneLine(string? text, int maximumLength)
    {
        var safe = VisibleControlText(text ?? string.Empty, Math.Max(1, maximumLength));
        var oneLine = safe.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return oneLine.Length <= maximumLength
            ? oneLine
            : string.Concat(oneLine.AsSpan(0, Math.Max(0, maximumLength - 1)), "…");
    }

    public static string VisibleControlText(string text, int maximumLength)
    {
        var builder = new System.Text.StringBuilder(Math.Min(text.Length, maximumLength));
        foreach (var character in text)
        {
            if (builder.Length >= maximumLength)
                break;

            if (character is '\n' or '\t')
            {
                builder.Append(character);
                continue;
            }

            builder.Append(char.IsControl(character)
                ? $"\\u{(int)character:X4}"
                : character);
        }

        if (text.Length > maximumLength)
            builder.Append('…');
        return builder.ToString();
    }

    public static string SemanticCopyText(string text) => VisibleControlText(text, int.MaxValue);

    public static string MarkdownToPlainText(string markdown)
    {
        var output = new StringBuilder(markdown.Length);
        var inCodeFence = false;
        foreach (var sourceLine in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = sourceLine.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            var line = inCodeFence
                ? $"  {sourceLine}"
                : MarkdownLineToPlainText(sourceLine);
            if (output.Length > 0)
                output.Append('\n');
            output.Append(line);
        }

        return output.ToString();
    }

    public static string BuildSemanticTurn(
        IReadOnlyList<ChatPresentationBlock> transcript,
        int selectedIndex)
    {
        if (transcript.Count == 0)
            return string.Empty;

        var boundedIndex = Math.Clamp(selectedIndex, 0, transcript.Count - 1);
        var start = boundedIndex;
        while (start > 0 && transcript[start].Kind != ChatBlockKind.User)
            start--;
        if (transcript[start].Kind != ChatBlockKind.User)
            start = boundedIndex;

        var end = boundedIndex + 1;
        while (end < transcript.Count && transcript[end].Kind != ChatBlockKind.User)
            end++;

        return string.Join(
            "\n\n",
            transcript.Skip(start).Take(end - start)
                .Select(block => SemanticCopyText(block.SemanticText)));
    }

    public static string CompactIdentity(string identity, int maximumLength) =>
        identity.Length <= maximumLength
            ? identity
            : $"…{identity[^Math.Max(1, maximumLength - 1)..]}";

    public static string ApprovalPath(
        ChatPresentationState state,
        ToolInteractionRequest approval)
    {
        const string subAgentMarker = "/subagent-approval/";
        var markerIndex = approval.CallId.Value.IndexOf(subAgentMarker, StringComparison.Ordinal);
        if (markerIndex <= 0)
            return approval.ToolName.Value;

        var parentCallId = approval.CallId.Value[..markerIndex];
        var requester = state.SubAgents.Values.FirstOrDefault(run =>
            string.Equals(run.ParentCallId, parentCallId, StringComparison.Ordinal));
        return requester is null
            ? $"sub-agent › {approval.ToolName.Value}"
            : $"{requester.AgentName} › {approval.ToolName.Value}";
    }

    private static Color LabelColor(ChatPresentationBlock block) => block.Kind switch
    {
        ChatBlockKind.User => Color.Cyan,
        ChatBlockKind.Assistant => Color.BrightBlue,
        ChatBlockKind.Thought => Color.Yellow,
        ChatBlockKind.Tool when block.IsFailure => Color.Red,
        ChatBlockKind.Tool => Color.Green,
        ChatBlockKind.Parallel => Color.BrightCyan,
        ChatBlockKind.SubAgent when block.IsFailure => Color.Red,
        ChatBlockKind.SubAgent => Color.Green,
        ChatBlockKind.Approval => Color.Yellow,
        ChatBlockKind.File => Color.Cyan,
        ChatBlockKind.Error => Color.Red,
        ChatBlockKind.Usage => Color.Gray,
        ChatBlockKind.Compaction => Color.Gray,
        ChatBlockKind.Diagnostic => Color.Red,
        _ => Color.Gray
    };

    private static Color BodyColor(ChatPresentationBlock block) => block.Kind switch
    {
        ChatBlockKind.Error or ChatBlockKind.Diagnostic => Color.Red,
        ChatBlockKind.Approval when block.IsFailure => Color.Red,
        ChatBlockKind.Usage or ChatBlockKind.Compaction => Color.Gray,
        _ => Color.White
    };

    private static string MarkdownLineToPlainText(string sourceLine)
    {
        var line = HeadingPrefixRegex().Replace(sourceLine, string.Empty);
        line = QuotePrefixRegex().Replace(line, "│ ");
        line = BulletPrefixRegex().Replace(line, "$1• ");
        line = MarkdownLinkRegex().Replace(line, "$1 <$2>");
        line = InlineCodeRegex().Replace(line, "$1");
        line = StrongRegex().Replace(line, "$2");
        line = StrikeRegex().Replace(line, "$1");
        return EmphasisRegex().Replace(line, "$2");
    }

    [GeneratedRegex(@"^[ \t]{0,3}#{1,6}[ \t]+")]
    private static partial Regex HeadingPrefixRegex();

    [GeneratedRegex(@"^[ \t]{0,3}>[ \t]?")]
    private static partial Regex QuotePrefixRegex();

    [GeneratedRegex(@"^([ \t]*)[-+*][ \t]+")]
    private static partial Regex BulletPrefixRegex();

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\(([^)\r\n]+)\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"`([^`\r\n]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?\S)\1")]
    private static partial Regex StrongRegex();

    [GeneratedRegex(@"~~(?=\S)(.+?\S)~~")]
    private static partial Regex StrikeRegex();

    [GeneratedRegex(@"(?<!\w)([*_])(?=\S)(.+?\S)\1(?!\w)")]
    private static partial Regex EmphasisRegex();
}
