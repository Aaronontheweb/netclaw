// -----------------------------------------------------------------------
// <copyright file="ChatPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Headless TUI tests for <see cref="ChatPage"/> using Termina's
/// <see cref="VirtualTerminal"/> and <see cref="VirtualInputSource"/>.
/// These exercise the Input-panel layout for pending approval interactions
/// (issue #1132): the bug was that a long <c>shell_execute</c> body wrapped
/// over many lines and pushed the selection list and key hints past the
/// 10-row Input panel cap, leaving the user unable to see <c>[Enter] Confirm</c>.
/// </summary>
public sealed class ChatPageTests
{
    // A representative long body that reproduces the original report: a `cd`
    // with many path arguments from kevin/code/compiler plus several macOS
    // temp paths. Well over 400 chars, so the pre-fix code wrapped it onto
    // 5+ rows and pushed the selection list off-screen.
    private const string LongShellBody =
        "cd /Users/kevin/code/compiler/Diagnostics.pn compiler/Symbol.pn compiler/Binder " +
        "/private/var/folders/pj/ncqg4f1s58l87j6n_9xvnz9m0000gn/T/tmp.aa " +
        "/private/var/folders/pj/ncqg4f1s58l87j6n_9xvnz9m0000gn/T/tmp.bb " +
        "/private/var/folders/pj/ncqg4f1s58l87j6n_9xvnz9m0000gn/T/tmp.cc " +
        "/private/var/folders/pj/ncqg4f1s58l87j6n_9xvnz9m0000gn/T/tmp.dd " +
        "/private/var/folders/pj/ncqg4f1s58l87j6n_9xvnz9m0000gn/T/tmp.ee " +
        "SENTINEL_TAIL_MARKER";

    [Fact]
    public async Task LongApprovalBody_KeepsControlsVisible()
    {
        var (terminal, app, _) = CreateHeadlessApp(BuildApproval(LongShellBody), out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();

        // The selection-list options must be on-screen INSIDE the Input panel
        // (the bottom slice), not just somewhere in the chat history pane
        // above. Pre-fix bug: the wrapped body filled the panel and the list
        // rendered past the bottom border, leaving the user unable to pick.
        var inputAndStatus = BottomRows(screen, terminalWidth: 120, rowCount: 14);
        AssertOptionVisible(inputAndStatus, "Once", terminal);
        AssertOptionVisible(inputAndStatus, "This chat", terminal);
        AssertOptionVisible(inputAndStatus, "Deny", terminal);

        // The status-bar Enter hint MUST stay visible — that's the key the
        // user needs to confirm their choice, and was the specific control
        // hidden in the original #1132 screenshot.
        Assert.True(inputAndStatus.Contains("[Enter] Confirm", StringComparison.Ordinal),
            $"Expected '[Enter] Confirm' hint in status bar. Screen:\n{terminal}");
    }

    [Fact]
    public async Task CollapsedView_ShowsEllipsisAndCtrlVHint()
    {
        var (terminal, app, _) = CreateHeadlessApp(BuildApproval(LongShellBody), out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();

        // Collapsed body must be truncated with an ellipsis marker. The body
        // contains "SENTINEL_TAIL_MARKER" at the very end; in collapsed mode
        // it MUST NOT be visible in the Input panel because it falls past the
        // truncation point. (It IS expected to be visible up in the chat
        // history pane, which always logs the full DisplayText on arrival —
        // that's the security audit trail, not the user-action surface.)
        Assert.True(screen.Contains('…'),
            $"Expected ellipsis '…' in collapsed body. Screen:\n{terminal}");

        var inputAndStatus = BottomRows(screen, terminalWidth: 120, rowCount: 14);
        Assert.True(!inputAndStatus.Contains("SENTINEL_TAIL_MARKER", StringComparison.Ordinal),
            $"Expected SENTINEL_TAIL_MARKER to be truncated out of the Input panel. " +
            $"Bottom rows:\n{inputAndStatus}\nFull screen:\n{terminal}");

        // The user needs to know how to see the full body. Both the inline
        // hint in the Input panel AND the status-bar hint advertise Ctrl+V.
        Assert.True(screen.Contains("Ctrl+V", StringComparison.Ordinal),
            $"Expected 'Ctrl+V' affordance to be visible. Screen:\n{terminal}");
    }

    /// <summary>
    /// Returns the last <paramref name="rowCount"/> rows of a
    /// <see cref="VirtualTerminal.ToString"/> dump. Used to scope assertions
    /// to the Input panel + status bar (the bottom slice), distinct from the
    /// always-full chat history (the top slice).
    /// </summary>
    private static string BottomRows(string screen, int terminalWidth, int rowCount)
    {
        var lines = screen.Split('\n');
        if (lines.Length <= rowCount)
            return screen;
        return string.Join('\n', lines.AsEnumerable().TakeLast(rowCount));
    }

    private static void AssertOptionVisible(string inputAndStatus, string optionLabel, VirtualTerminal terminal)
    {
        Assert.True(inputAndStatus.Contains(optionLabel, StringComparison.Ordinal),
            $"Expected approval option '{optionLabel}' to render inside the Input panel " +
            $"(bottom slice of the terminal). Bottom rows:\n{inputAndStatus}\nFull screen:\n{terminal}");
    }

    [Fact]
    public async Task CtrlV_TogglesFullBodyAndKeepsControlsVisible()
    {
        var (terminal, app, vm) = CreateHeadlessApp(BuildApproval(LongShellBody), out var input);

        // Ctrl+V to expand, then quit. The toggle happens before the next
        // render, so by the time the app shuts down the terminal holds the
        // expanded frame.
        input.EnqueueKey(ConsoleKey.V, false, false, true);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();

        // Controls remain visible even in expanded mode — that's the whole
        // point: Ctrl+V must not regress the original bug. Scope assertions
        // to the bottom of the screen so we're checking the Input panel,
        // not the chat history pane that always echoes the full body.
        var inputAndStatus = BottomRows(screen, terminalWidth: 120, rowCount: 14);
        AssertOptionVisible(inputAndStatus, "Once", terminal);
        AssertOptionVisible(inputAndStatus, "This chat", terminal);
        AssertOptionVisible(inputAndStatus, "Deny", terminal);
        Assert.True(inputAndStatus.Contains("[Enter] Confirm", StringComparison.Ordinal),
            $"Expected '[Enter] Confirm' still visible after expand. Screen:\n{terminal}");

        // The status-bar hint should now read "Collapse" instead of "View full".
        Assert.True(screen.Contains("Collapse", StringComparison.Ordinal),
            $"Expected status hint to flip to 'Collapse' after expand. Screen:\n{terminal}");

        Assert.True(vm.IsApprovalDetailVisible.Value);
    }

    [Fact]
    public void NewInteraction_ResetsCollapsedState()
    {
        // This case operates directly on the ViewModel — the goal is to
        // verify that consecutive ToolInteractionRequest arrivals do not
        // preserve a previous expanded state, so each new approval starts
        // collapsed with controls visible by default.
        var vm = new TestChatViewModel(seed: null);
        vm.SeedPendingInteractionForTesting(BuildApproval("first body"));
        vm.ToggleApprovalDetail();
        Assert.True(vm.IsApprovalDetailVisible.Value);

        vm.SeedPendingInteractionForTesting(BuildApproval("second body"));
        Assert.False(vm.IsApprovalDetailVisible.Value);
    }

    [Fact]
    public async Task LongApprovalBody_RenderedFrameSnapshot()
    {
        // Captures both the collapsed and expanded rendered frames for the
        // long-body case and writes them as ASCII snapshots under the test
        // project's __snapshots__ directory. Doubles as the visual artifact
        // for issue #1132: paste the .txt into a GH comment as a fenced
        // code block to show what the fixed UI looks like.
        var collapsed = await CaptureFrameAsync(expand: false);
        var expanded = await CaptureFrameAsync(expand: true);

        WriteSnapshot("chat-approval-collapsed.txt", collapsed);
        WriteSnapshot("chat-approval-expanded.txt", expanded);
    }

    private async Task<string> CaptureFrameAsync(bool expand)
    {
        var (terminal, app, _) = CreateHeadlessApp(BuildApproval(LongShellBody), out var input);
        if (expand)
            input.EnqueueKey(ConsoleKey.V, false, false, true);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        return terminal.ToString();
    }

    private static void WriteSnapshot(string filename, string content)
    {
        // Walk up from the test bin/ to the project's Tui directory.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        DirectoryInfo? projectDir = null;
        while (current is not null)
        {
            if (current.GetFiles("Netclaw.Cli.Tests.csproj").Length > 0)
            {
                projectDir = current;
                break;
            }
            current = current.Parent;
        }

        // Fall back to bin/ if the source tree isn't reachable (e.g. when
        // running off a published test bundle).
        var targetDir = projectDir is not null
            ? Path.Combine(projectDir.FullName, "Tui", "__snapshots__")
            : Path.Combine(AppContext.BaseDirectory, "__snapshots__");
        Directory.CreateDirectory(targetDir);

        File.WriteAllText(Path.Combine(targetDir, filename), content);
    }

    private static ToolInteractionRequest BuildApproval(string displayText)
    {
        return new ToolInteractionRequest
        {
            SessionId = new SessionId("test-session"),
            TimestampMs = 0,
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId("test-call"),
            ToolName = new Netclaw.Tools.ToolName("shell_execute"),
            DisplayText = displayText,
            Patterns = ["cd"],
            CandidateVerbs = ["cd"],
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };
    }

    private static (VirtualTerminal Terminal, TerminaApplication App, TestChatViewModel Vm)
        CreateHeadlessApp(ToolInteractionRequest? seed, out VirtualInputSource input)
    {
        // 120x40 matches the existing ApprovalsManagerPageTests harness and
        // gives the Input panel its full 10-row cap with room to spare.
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        TestChatViewModel? capturedVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/chat", builder =>
        {
            builder.RegisterRoute<ChatPage, ChatViewModel>(
                "/chat",
                _ => new ChatPage(),
                _ =>
                {
                    capturedVm = new TestChatViewModel(seed);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }

    /// <summary>
    /// ChatViewModel subclass that bypasses daemon initialization for headless
    /// tests. The real <see cref="ChatViewModel.InitializeSessionAsync"/> opens
    /// a SignalR connection and subscribes to live daemon output; we override
    /// it to a no-op and stage a pre-baked <see cref="ToolInteractionRequest"/>
    /// via <c>SeedPendingInteractionForTesting</c> instead.
    /// </summary>
    private sealed class TestChatViewModel : ChatViewModel
    {
        private readonly ToolInteractionRequest? _seed;

        public TestChatViewModel(ToolInteractionRequest? seed)
            : base(
                // 127.0.0.1:1 is never dialed: InitializeSessionAsync is
                // overridden to no-op, so the underlying HubConnection stays
                // dormant. The DaemonClient constructor only validates that
                // the endpoint string is non-empty.
                new DaemonClient("http://127.0.0.1:1"),
                TimeProvider.System,
                new ModelCapabilities { ModelId = "test-model" },
                new ChatNavigationState())
        {
            _seed = seed;
        }

        protected override Task InitializeSessionAsync() => Task.CompletedTask;

        public override void OnActivated()
        {
            base.OnActivated();
            if (_seed is not null)
                SeedPendingInteractionForTesting(_seed);
        }
    }
}
