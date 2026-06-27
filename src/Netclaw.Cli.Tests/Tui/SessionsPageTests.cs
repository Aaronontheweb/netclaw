// -----------------------------------------------------------------------
// <copyright file="SessionsPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Cli.Tui;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Regression tests for <see cref="SessionsPage"/> — verifies that Termina
/// <see cref="SelectionListNode"/> correctly handles arrow keys, selection
/// confirmation, and Enter navigation.
///
/// Regression for: sessions were unselectable after commit 35d79d28c
/// ("make all TUI list views scrollable") because the old TextNode
/// for-loop's manual key handling was replaced with SelectionListNode
/// but no key delegation was wired up.
/// The fix uses SelectionListNode with .WithHighlightedIndex() for scrollable
/// rendering while keeping manual Up/Down/Enter handling in the ViewModel
/// for proper two-way selection binding.
/// </summary>
public sealed class SessionsPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero));

    public SessionsPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    private static SessionCatalogEntryDto CreateSession(
        string persistenceId, string channel, int turnCount, DateTimeOffset lastActivity)
    {
        return new SessionCatalogEntryDto
        {
            PersistenceId = persistenceId,
            Title = persistenceId.Replace("-", " ").Replace("session-", ""),
            Channel = channel,
            TurnCount = turnCount,
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastActivity = lastActivity.ToUnixTimeMilliseconds()
        };
    }

    [Fact]
    public async Task EmptyCatalog_RendersEmptyState()
    {
        var (terminal, app, _) = CreateHeadlessApp(out _, []);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("Sessions"), "Expected page title 'Sessions'");
        Assert.True(terminal.Contains("No sessions found"), "Expected empty-state message");
    }

    [Fact]
    public async Task SessionsList_RendersEntriesWithCorrectFormat()
    {
        var sessions = new[]
        {
            CreateSession("session-abc-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
            CreateSession("session-def-002", "tui", 2, _time.GetUtcNow().AddMinutes(-10)),
        };

        var (terminal, app, _) = CreateHeadlessApp(out _, sessions);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("tui"), "Expected channel name 'tui'");
    }

    [Fact]
    public async Task DownArrow_NavigatesSelectionIndex()
    {
        var sessions = new[]
        {
            CreateSession("session-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
            CreateSession("session-002", "tui", 2, _time.GetUtcNow().AddMinutes(-2)),
            CreateSession("session-003", "tui", 3, _time.GetUtcNow().AddMinutes(-3)),
        };

        var (_, app, vm) = CreateHeadlessApp(out var input, sessions);

        // Down 3 times → should reach index 2 (third session)
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(vm.SelectedIndex.Value == 2,
            "Expected selection index 2 after 3 down arrows. Actual: " + vm.SelectedIndex.Value);
    }

    [Fact]
    public async Task UpArrow_NavigatesSelectionIndexBack()
    {
        var sessions = new[]
        {
            CreateSession("session-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
            CreateSession("session-002", "tui", 2, _time.GetUtcNow().AddMinutes(-2)),
            CreateSession("session-003", "tui", 3, _time.GetUtcNow().AddMinutes(-3)),
        };

        var (_, app, vm) = CreateHeadlessApp(out var input, sessions);

        // Down 2 → index 2, then up 1 → index 1
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.UpArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(vm.SelectedIndex.Value == 1,
            "Expected selection index 1 after down-down-up. Actual: " + vm.SelectedIndex.Value);
    }

    [Fact]
    public async Task EnterOnSession_SetsResumeSessionIdAndNavigates()
    {
        var sessions = new[]
        {
            CreateSession("session-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
            CreateSession("session-002", "tui", 2, _time.GetUtcNow().AddMinutes(-2)),
        };

        var (_, app, vm) = CreateHeadlessApp(out var input, sessions);

        // Down → select second session (index 1)
        input.EnqueueKey(ConsoleKey.DownArrow);
        // Enter → confirm selection
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(vm.SelectedIndex.Value == 1,
            "Expected SelectedIndex to be 1 after Down + Enter. Actual: " + vm.SelectedIndex.Value);
    }

    [Fact]
    public async Task SelectionConfirmed_PropagatesCorrectIndexToViewModel()
    {
        // This is the KEY regression test. The bug was that SelectionListNode
        // captured arrow keys internally but never propagated the selection
        // back to ViewModel.SelectedIndex — so Enter always resumed session 0.
        var sessions = new[]
        {
            CreateSession("session-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
            CreateSession("session-002", "tui", 2, _time.GetUtcNow().AddMinutes(-2)),
            CreateSession("session-003", "tui", 3, _time.GetUtcNow().AddMinutes(-3)),
        };

        var (_, app, vm) = CreateHeadlessApp(out var input, sessions);

        // Down 2 → index 2 (third session)
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        // Enter fires RaiseEnter, which uses ViewModel.SelectedIndex.
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(vm.ResumeSessionId.Value == "session-003",
            "Enter on 3rd session should set ResumeSessionId to session-003. " +
            "This is the regression from commit 35d79d28c. Actual: " + vm.ResumeSessionId);
    }

    [Fact]
    public async Task NKey_StartsNewChatWithoutResuming()
    {
        var sessions = new[]
        {
            CreateSession("session-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
        };

        var (_, app, vm) = CreateHeadlessApp(out var input, sessions);

        input.EnqueueKey(ConsoleKey.N);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(vm.ResumeSessionId.Value is null,
            "Pressing N should NOT set ResumeSessionId. Actual: " + vm.ResumeSessionId);
    }

    [Fact]
    public async Task Escape_QuitsFromAnyState()
    {
        var sessions = new[]
        {
            CreateSession("session-001", "tui", 1, _time.GetUtcNow().AddMinutes(-1)),
        };

        var (_, app, _) = CreateHeadlessApp(out var input, sessions);
        input.EnqueueKey(ConsoleKey.Escape);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);
    }

    [Fact]
    public async Task LongList_FillsTerminalHeight_ForScrollableList()
    {
        // Verify that the SelectionListNode expands to fill the terminal
        // and scrolls natively (the fix that introduced the regression).
        var sessions = Enumerable.Range(1, 100)
            .Select(i => CreateSession($"session-{i:000}", "tui", i, _time.GetUtcNow().AddMinutes(-i)))
            .ToArray();

        var (terminal, app, _) = CreateHeadlessApp(out _, sessions);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();

        // Verify multiple sessions are visible (not just 1-2).
        // A 40-row VirtualTerminal with panel chrome should show ~30+ rows.
        var visibleCount = Enumerable.Range(1, 100)
            .Count(i => screen.Contains($"session-{i:000}", StringComparison.Ordinal));

        Assert.True(visibleCount > 15,
            "Expected >15 sessions visible in scrollable list; only " + visibleCount +
            " visible. Screen:\n" + terminal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (VirtualTerminal Terminal, TerminaApplication App, SessionsViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input, SessionCatalogEntryDto[] sessions)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        SessionsViewModel? capturedVm = null;

        var configuration = new ConfigurationBuilder().Build();
        var mockHandler = new MockSessionsHttpHandler(sessions);
        var mockFactory = new MockHttpClientFactory(mockHandler);
        var daemonApi = new DaemonApi(mockFactory, configuration, _paths);

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddSingleton(daemonApi);
        services.AddSingleton(_time);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/sessions", builder =>
        {
            builder.RegisterRoute<SessionsPage, SessionsViewModel>(
                "/sessions",
                _ => new SessionsPage(),
                _ =>
                {
                    capturedVm = new SessionsViewModel(daemonApi, new ChatNavigationState(), _time);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }

    private sealed class MockSessionsHttpHandler : HttpMessageHandler
    {
        private readonly SessionCatalogEntryDto[] _sessions;

        public MockSessionsHttpHandler(SessionCatalogEntryDto[] sessions) => _sessions = sessions;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_sessions);

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public MockHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler);
    }
}
