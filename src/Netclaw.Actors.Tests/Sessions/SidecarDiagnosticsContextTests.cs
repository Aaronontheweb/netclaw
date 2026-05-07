// -----------------------------------------------------------------------
// <copyright file="SidecarDiagnosticsContextTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Configuration;
using Xunit;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Verifies that session-owned sidecar paths populate
/// <see cref="SessionDiagnosticsContext"/> around their <c>IChatClient</c>
/// calls so MEL provider diagnostics emitted during the call route into
/// the per-session log. One test per major sidecar covered by
/// netclaw-dev/netclaw#920. Test contract: a fake <c>IChatClient</c>
/// captures <c>SessionDiagnosticsContext.SessionId</c> at the moment the
/// chat client method is invoked. That is exactly the AsyncLocal value
/// any real provider plugin would see when emitting MEL log lines.
/// </summary>
public sealed class SidecarDiagnosticsContextTests : TestKit
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task TitleGenerator_populates_session_diagnostics_scope()
    {
        var sessionId = new SessionId("ch/title-thread");
        var captor = new SessionContextCapturingChatClient();
        var probe = CreateTestProbe();

        SessionDiagnosticsContext.SessionId = null;
        await SessionTitleGenerator.GenerateAsync(
            captor,
            sessionId,
            history: [],
            self: probe.Ref,
            log: NoLogger.Instance,
            timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(sessionId.Value, captor.CapturedSessionId);
        Assert.Null(SessionDiagnosticsContext.SessionId);
    }

    [Fact]
    public async Task CompactionPipeline_populates_session_diagnostics_scope()
    {
        var sessionId = new SessionId("ch/compaction-thread");
        var captor = new SessionContextCapturingChatClient();
        var history = new List<SerializableChatMessage>
        {
            new() { Role = Netclaw.Actors.Protocol.ChatRole.User, Content = "hello" },
            new() { Role = Netclaw.Actors.Protocol.ChatRole.Assistant, Content = "hi" }
        };

        SessionDiagnosticsContext.SessionId = null;
        var observation = await SessionCompactionPipeline.GenerateObservationsAsync(
            client: captor,
            sessionId: sessionId,
            history: history,
            systemOffset: 0,
            keepStartIndex: 1,
            sidecarTimeout: TimeSpan.FromSeconds(5),
            log: NoLogger.Instance,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(sessionId.Value, captor.CapturedSessionId);
        Assert.Null(SessionDiagnosticsContext.SessionId);
        Assert.NotNull(observation);
    }

    /// <summary>
    /// IChatClient stub that captures <see cref="SessionDiagnosticsContext.SessionId"/>
    /// at the moment its methods are invoked. The captured value is the
    /// AsyncLocal seen by the chat client — the same value any MEL provider
    /// plugin emitting diagnostics inside the call would see.
    /// </summary>
    private sealed class SessionContextCapturingChatClient : IChatClient
    {
        public string? CapturedSessionId { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CapturedSessionId = SessionDiagnosticsContext.SessionId;
            var response = new ChatResponse(new AiChatMessage(
                AiChatRole.Assistant,
                (IList<AIContent>)[new TextContent("captured")]));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => StreamAsync(cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CapturedSessionId = SessionDiagnosticsContext.SessionId;
            yield return new ChatResponseUpdate(AiChatRole.Assistant, "captured");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
