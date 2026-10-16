// -----------------------------------------------------------------------
// <copyright file="ProviderOAuthTokenRefreshService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Providers.OAuth;

/// <summary>
/// Refreshes persisted OAuth credentials for inference providers.
/// </summary>
public sealed class ProviderOAuthTokenRefreshService(
    NetclawPaths paths,
    DeviceFlowServiceFactory deviceFlowFactory,
    ILogger<ProviderOAuthTokenRefreshService> logger,
    IOperationalNotificationSink? notificationSink = null,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    private readonly ILogger<ProviderOAuthTokenRefreshService> _logger = logger;
    private readonly IOperationalNotificationSink _notificationSink = notificationSink ?? NullNotificationSink.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SensitiveString> GetValidAccessTokenAsync(
        string providerName,
        ProviderEntry entry,
        OAuthAuth oauth,
        CancellationToken ct = default)
        => await GetAccessTokenAsync(providerName, entry, oauth, forceRefresh: false, expectedAccessToken: null, ct);

    /// <summary>
    /// Refreshes the provider token after a request was rejected with HTTP 401.
    /// If another request already replaced the token while this call waited for the
    /// provider lock, the newer token is reused instead of a second refresh.
    /// </summary>
    public async Task<SensitiveString> ForceRefreshAccessTokenAsync(
        string providerName,
        ProviderEntry entry,
        OAuthAuth oauth,
        string rejectedAccessToken,
        CancellationToken ct = default)
        => await GetAccessTokenAsync(providerName, entry, oauth, forceRefresh: true, expectedAccessToken: rejectedAccessToken, ct);

    private async Task<SensitiveString> GetAccessTokenAsync(
        string providerName,
        ProviderEntry entry,
        OAuthAuth oauth,
        bool forceRefresh,
        string? expectedAccessToken,
        CancellationToken ct)
    {
        var accessToken = entry.OAuthAccessToken.RequireValid(
            $"OAuth access token for provider '{providerName}'");

        if (!forceRefresh && !NeedsRefresh(entry.OAuthTokenExpiry))
            return accessToken;

        // Coalesce concurrent refreshes for the same configured provider so a
        // burst of requests does not spend the same refresh token multiple times.
        var refreshGate = _refreshLocks.GetOrAdd(providerName, _ => new SemaphoreSlim(1, 1));
        await refreshGate.WaitAsync(ct);
        try
        {
            var displacedRefreshToken = AdoptStoredTokens(providerName, entry);
            accessToken = entry.OAuthAccessToken.RequireValid(
                $"OAuth access token for provider '{providerName}'");

            // A forced refresh that raced a concurrent refresh already replaced the
            // token, so reuse the newer token instead of refreshing again. A second
            // refresh would spend the now-rotated refresh token and fail.
            if (forceRefresh
                && expectedAccessToken is not null
                && !string.Equals(accessToken.Value, expectedAccessToken, StringComparison.Ordinal))
            {
                return accessToken;
            }

            if (!forceRefresh && !NeedsRefresh(entry.OAuthTokenExpiry))
                return accessToken;

            if (entry.OAuthRefreshToken.IsNullOrEmpty())
            {
                EmitAuthExpired(providerName, "no_refresh_token");
                throw new ProviderOAuthRefreshRequiredException(
                    $"OAuth token for provider '{providerName}' expired with no refresh token. "
                    + $"Re-authenticate '{providerName}' with `netclaw provider`.");
            }

            var service = deviceFlowFactory.GetFor(oauth);
            var result = await service.RefreshTokenAsync(
                oauth.TokenEndpoint.ToString(),
                oauth.ClientId,
                entry.OAuthRefreshToken,
                ct);

            // A refresh that worked at the token server can fail to reach secrets.json. Then the stored
            // refresh token is spent and the in-memory one is the live one. Try the token that
            // AdoptStoredTokens replaced, once, before the operator has to sign in again.
            if (result is null && !displacedRefreshToken.IsNullOrEmpty())
            {
                result = await service.RefreshTokenAsync(
                    oauth.TokenEndpoint.ToString(),
                    oauth.ClientId,
                    displacedRefreshToken,
                    ct);

                if (result is not null)
                {
                    _logger.LogWarning(
                        "The refresh token stored in {SecretsPath} for provider {Provider} was rejected, and the in-memory refresh token worked. "
                        + "An earlier save of refreshed tokens probably failed.",
                        paths.SecretsPath,
                        providerName);
                    entry.OAuthRefreshToken = displacedRefreshToken;
                }
            }

            if (result is null)
            {
                EmitAuthExpired(providerName, "invalid_grant");
                throw new ProviderOAuthRefreshRequiredException(
                    $"OAuth refresh token for provider '{providerName}' was rejected. "
                    + $"Re-authenticate '{providerName}' with `netclaw provider`.");
            }

            ApplyRefreshResult(entry, result);
            try
            {
                // A protector is mandatory here. Passing none used to skip encryption, so a
                // refreshed access and refresh token pair landed in secrets.json as plaintext
                // until some later protected write happened to re-encrypt the file. The refresh token
                // is written even when the server did not send one, so the file never keeps a spent token.
                OAuthTokenPersistence.PersistTokens(
                    paths,
                    providerName,
                    result with { RefreshToken = entry.OAuthRefreshToken },
                    SecretsProtection.CreateProtector(paths));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "The OAuth refresh for provider {Provider} worked, but Netclaw could not save the new tokens to {SecretsPath} or the expiry to {OAuthStatePath}. "
                    + "The saved file can hold a spent refresh token. Fix the file or its directory. Until then this process uses the in-memory tokens.",
                    providerName,
                    paths.SecretsPath,
                    paths.OAuthStatePath);
                throw;
            }

            return entry.OAuthAccessToken!;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    // Another process, such as `netclaw provider`, can refresh the same provider. The token server
    // rotates the refresh token, so the in-memory copy can be spent already. Returns the in-memory
    // refresh token that the stored one replaced, so the caller can try it if the stored one fails.
    private SensitiveString? AdoptStoredTokens(string providerName, ProviderEntry entry)
    {
        if (OAuthTokenPersistence.LoadTokens(paths, providerName) is not { } stored)
            return null;

        SensitiveString? displaced = null;
        if (!stored.RefreshToken.IsNullOrEmpty()
            && !string.Equals(stored.RefreshToken.Value, entry.OAuthRefreshToken?.Value, StringComparison.Ordinal))
        {
            displaced = entry.OAuthRefreshToken;
            entry.OAuthRefreshToken = stored.RefreshToken;
        }

        if (stored.ExpiresAt is { } storedExpiry && (entry.OAuthTokenExpiry is not { } expiry || storedExpiry > expiry))
        {
            entry.OAuthAccessToken = stored.AccessToken;
            entry.OAuthTokenExpiry = storedExpiry;
            entry.OAuthAccountId = stored.AccountId ?? entry.OAuthAccountId;
        }

        return displaced;
    }

    private bool NeedsRefresh(DateTimeOffset? expiresAt)
        => expiresAt is not null && expiresAt.Value - RefreshBuffer <= _timeProvider.GetUtcNow();

    private static void ApplyRefreshResult(ProviderEntry entry, OAuthDeviceFlowResult result)
    {
        var existingRefreshToken = entry.OAuthRefreshToken;
        var existingAccountId = entry.OAuthAccountId;

        entry.OAuthAccessToken = result.AccessToken;
        entry.OAuthRefreshToken = result.RefreshToken ?? existingRefreshToken;
        entry.OAuthTokenExpiry = result.ExpiresAt;
        entry.OAuthAccountId = result.AccountId ?? existingAccountId;
    }

    private void EmitAuthExpired(string providerName, string reason)
        => _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "provider.auth.expired",
            AlertType.ProviderAuthExpired,
            $"OAuth credentials for provider '{providerName}' require re-authentication. Run `netclaw provider`, or `netclaw provider add {providerName} <type> --auth oauth-device` to re-authenticate in place.",
            AlertSeverity.Warning,
            source: providerName,
            context: new Dictionary<string, string>
            {
                ["providerName"] = providerName,
                ["reason"] = reason,
            }));
}
