// -----------------------------------------------------------------------
// <copyright file="OAuthTokenRefreshRejectedException.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;

namespace Netclaw.Providers.OAuth;

/// <summary>
/// Thrown when an OAuth token endpoint terminally rejects a refresh request
/// (RFC 6749 §5.2): <c>invalid_grant</c> (the grant was revoked or the rotating
/// refresh token already consumed) or <c>invalid_client</c> /
/// <c>unauthorized_client</c> (the client registration itself is dead — e.g. a
/// provider purged its dynamically-registered client IDs). Retrying with the
/// same refresh token / client_id can never succeed, so callers must clear the
/// stored credentials and re-authorize. Transient failures (5xx, network
/// errors, unrecognized or unparsable error bodies) surface as
/// <see cref="HttpRequestException"/> instead.
/// </summary>
public sealed class OAuthTokenRefreshRejectedException : Exception
{
    public OAuthTokenRefreshRejectedException(string errorCode, HttpStatusCode statusCode)
        : base($"Token endpoint terminally rejected the refresh request: {errorCode} (HTTP {(int)statusCode})")
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    /// <summary>The OAuth error code returned by the token endpoint.</summary>
    public string ErrorCode { get; }

    /// <summary>The HTTP status code of the rejection response.</summary>
    public HttpStatusCode StatusCode { get; }
}
