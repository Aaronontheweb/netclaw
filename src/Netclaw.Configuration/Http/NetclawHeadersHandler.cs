// -----------------------------------------------------------------------
// <copyright file="NetclawHeadersHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration.Http;

/// <summary>
/// <see cref="DelegatingHandler"/> that adds the shared Netclaw User-Agent
/// and a component identifier header to every outgoing request. Existing
/// User-Agent values on the request are preserved — callers that intentionally
/// spoof a UA (e.g. web fetch or DDG scraping) bypass the header by not
/// registering this handler in the first place.
/// </summary>
public sealed class NetclawHeadersHandler : DelegatingHandler
{
    private readonly string _component;

    public NetclawHeadersHandler(string component)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        _component = component;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Headers.UserAgent.Count == 0)
            request.Headers.TryAddWithoutValidation("User-Agent", NetclawUserAgent.Value);

        if (!request.Headers.Contains(NetclawUserAgent.ComponentHeader))
            request.Headers.TryAddWithoutValidation(NetclawUserAgent.ComponentHeader, _component);

        return base.SendAsync(request, cancellationToken);
    }
}
