// -----------------------------------------------------------------------
// <copyright file="NetclawHeadersHandlerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration.Http;
using Xunit;

namespace Netclaw.Configuration.Tests.Http;

public sealed class NetclawHeadersHandlerTests
{
    [Fact]
    public async Task Adds_user_agent_and_component_headers_when_absent()
    {
        var captured = new CapturingHandler();
        var handler = new NetclawHeadersHandler("test-component")
        {
            InnerHandler = captured,
        };

        using var client = new HttpClient(handler);
        await client.GetAsync("https://example.invalid/", TestContext.Current.CancellationToken);

        Assert.Equal(NetclawUserAgent.Value, captured.LastRequest!.Headers.UserAgent.ToString());
        Assert.True(captured.LastRequest.Headers.TryGetValues(NetclawUserAgent.ComponentHeader, out var values));
        Assert.Equal("test-component", Assert.Single(values));
    }

    [Fact]
    public async Task Preserves_caller_supplied_user_agent()
    {
        var captured = new CapturingHandler();
        var handler = new NetclawHeadersHandler("test-component")
        {
            InnerHandler = captured,
        };

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        request.Headers.UserAgent.ParseAdd("CustomAgent/9.9");

        await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Contains(
            captured.LastRequest!.Headers.UserAgent,
            p => p.Product?.Name == "CustomAgent");
        Assert.DoesNotContain(
            captured.LastRequest.Headers.UserAgent,
            p => p.Product?.Name == "Netclaw");
    }

    [Fact]
    public async Task Preserves_caller_supplied_component_header()
    {
        var captured = new CapturingHandler();
        var handler = new NetclawHeadersHandler("default-component")
        {
            InnerHandler = captured,
        };

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        request.Headers.TryAddWithoutValidation(NetclawUserAgent.ComponentHeader, "override");

        await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(captured.LastRequest!.Headers.TryGetValues(NetclawUserAgent.ComponentHeader, out var values));
        Assert.Equal("override", Assert.Single(values));
    }

    [Fact]
    public void DI_extension_registers_handler_for_named_client()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("test").AddNetclawHeaders("test-component");

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var client = factory.CreateClient("test");
        Assert.NotNull(client);
    }

    [Fact]
    public void DI_extension_rejects_empty_component()
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("test");

        Assert.Throws<ArgumentException>(() => builder.AddNetclawHeaders(""));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
