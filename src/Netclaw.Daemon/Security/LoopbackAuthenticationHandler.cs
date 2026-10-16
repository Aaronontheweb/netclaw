// -----------------------------------------------------------------------
// <copyright file="LoopbackAuthenticationHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Security;

/// <summary>
/// ASP.NET Core authentication handler that trusts connections originating from the
/// loopback interface (<c>127.0.0.1</c> / <c>::1</c>).
///
/// Loopback connections are granted <c>Operator</c> + <c>LocalProcess</c> identity
/// because only a process running on the same machine can reach the loopback address.
/// Non-loopback connections receive <see cref="AuthenticateResult.NoResult"/> so that
/// other schemes (e.g. device bearer token, future OAuth) can take over.
///
/// A loopback source address does not prove that the operator made the request: a web
/// page in the operator's browser can reach the daemon by DNS rebinding or by a cross-site
/// request. Both carry a foreign <c>Host</c> or <c>Origin</c> header, so
/// <see cref="LoopbackHostOriginPolicy"/> refuses them.
/// </summary>
public sealed class LoopbackAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Loopback";
    private readonly DaemonConfig _daemonConfig;

    public LoopbackAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        DaemonConfig daemonConfig)
        : base(options, logger, encoder)
    {
        _daemonConfig = daemonConfig;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Any exposure mode that accepts remote traffic must never inherit
        // loopback operator trust. Tunnel agents (tailscaled, cloudflared) and
        // reverse proxies all forward remote connections over the loopback
        // socket, so a loopback source address is NOT proof of a same-host
        // caller in those modes. Remote traffic must flow through a
        // remote-authenticated scheme (the device bearer token) instead — fail
        // closed here. Only ExposureMode.Local treats loopback as truly local.
        if (_daemonConfig.ExposureMode.RequiresRemoteAuthentication())
            return Task.FromResult(AuthenticateResult.NoResult());

        var remoteIp = Context.Connection.RemoteIpAddress;

        if (remoteIp is null || !IsLoopback(remoteIp))
            return Task.FromResult(AuthenticateResult.NoResult());

        var request = Context.Request;
        var refusal = LoopbackHostOriginPolicy.FindRefusal(request.Host, request.Headers.Origin);
        if (refusal is not null)
        {
            Logger.LogWarning(
                "Refused loopback request: {Reason}. Host={Host} Origin={Origin}",
                refusal,
                request.Host.Value,
                request.Headers.Origin.ToString());
            return Task.FromResult(AuthenticateResult.Fail(refusal));
        }

        var claims = new[]
        {
            new Claim(NetclawClaimTypes.PrincipalClassification,
                nameof(PrincipalClassification.Operator)),
            new Claim(NetclawClaimTypes.TransportAuthenticity,
                nameof(TransportAuthenticity.LocalProcess)),
            new Claim(NetclawClaimTypes.DeviceId, "local"),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool IsLoopback(IPAddress address)
        => IPAddress.IsLoopback(address)
           || address.Equals(IPAddress.IPv6Loopback);
}

/// <summary>
/// The fixed Host and Origin rule for loopback operator trust. A browser always sends a
/// <c>Host</c> equal to the name in its address bar and an <c>Origin</c> that names the
/// page that made a cross-site request. The daemon serves plain HTTP in Local mode, so
/// the only valid Origin is <c>http://</c> plus the request Host.
/// </summary>
internal static class LoopbackHostOriginPolicy
{
    public static string? FindRefusal(HostString host, StringValues origin)
    {
        if (!IsLoopbackHostName(host))
            return "Host is not a loopback name";

        if (origin.Count == 0)
            return null;

        var expected = "http://" + host.Value;
        if (origin.Count != 1 || !string.Equals(origin[0], expected, StringComparison.OrdinalIgnoreCase))
            return "Origin does not match Host";

        return null;
    }

    private static bool IsLoopbackHostName(HostString host)
    {
        if (!host.HasValue)
            return false;

        var name = host.Host;
        if (!IsEmptyOrPortSuffix(host.Value.AsSpan(name.Length)))
            return false;

        return string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "127.0.0.1", StringComparison.Ordinal)
               || string.Equals(name, "[::1]", StringComparison.Ordinal);
    }

    private static bool IsEmptyOrPortSuffix(ReadOnlySpan<char> suffix)
    {
        if (suffix.IsEmpty)
            return true;

        if (suffix[0] != ':' || suffix.Length == 1 || suffix.Length > 6)
            return false;

        foreach (var c in suffix[1..])
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }
}
