// -----------------------------------------------------------------------
// <copyright file="StreamFirstChatClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class StreamFirstChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_is_served_by_aggregating_the_stream()
    {
        var inner = new StreamOnlyChatClient();
        var client = new StreamFirstChatClient(inner);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        // The native non-streaming path must never be touched — it's the path that
        // 400s against streaming-only backends.
        Assert.False(inner.NonStreamingInvoked);
        Assert.True(inner.StreamingInvoked);
        Assert.Single(response.Messages); // aggregated into one assistant message, not split
        Assert.Equal("Hello world", response.Text);
        Assert.Equal(7, response.Usage?.InputTokenCount);
        Assert.Equal(3, response.Usage?.OutputTokenCount);
    }

    [Fact]
    public async Task GetResponseAsync_on_empty_stream_yields_a_single_empty_assistant_message()
    {
        var client = new StreamFirstChatClient(new StreamOnlyChatClient(emptyStream: true));

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        // Callers index Messages[^1] unguarded; an empty completion must not throw.
        var last = response.Messages[^1];
        Assert.Equal(ChatRole.Assistant, last.Role);
        Assert.True(string.IsNullOrEmpty(last.Text));
    }

    [Fact]
    public async Task GetStreamingResponseAsync_passes_through_unchanged()
    {
        var inner = new StreamOnlyChatClient();
        var client = new StreamFirstChatClient(inner);

        var texts = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            foreach (var content in update.Contents)
                if (content is TextContent t)
                    texts.Add(t.Text);
        }

        Assert.True(inner.StreamingInvoked);
        Assert.False(inner.NonStreamingInvoked);
        Assert.Equal(new[] { "Hello", " world" }, texts);
    }

    /// <summary>
    /// A streaming-only client: <see cref="GetResponseAsync"/> throws (mirrors a backend
    /// that rejects non-streaming), while <see cref="GetStreamingResponseAsync"/> yields
    /// text deltas followed by usage.
    /// </summary>
    private sealed class StreamOnlyChatClient(bool emptyStream = false) : IChatClient
    {
        public bool StreamingInvoked { get; private set; }
        public bool NonStreamingInvoked { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            NonStreamingInvoked = true;
            throw new InvalidOperationException("Stream must be set to true");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingInvoked = true;
            await Task.CompletedTask;
            if (emptyStream)
                yield break;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello");
            yield return new ChatResponseUpdate(ChatRole.Assistant, " world");
            yield return new ChatResponseUpdate
            {
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 })]
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
