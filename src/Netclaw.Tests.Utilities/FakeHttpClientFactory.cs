// -----------------------------------------------------------------------
// <copyright file="FakeHttpClientFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tests.Utilities;

internal sealed class FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
        => new(new FakeHttpMessageHandler(handler));
}
