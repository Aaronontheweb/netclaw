// -----------------------------------------------------------------------
// <copyright file="OAuthStateStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netclaw.Configuration;

/// <summary>
/// Reads and writes oauth-state.json, which holds the token expiry of each OAuth provider.
/// </summary>
/// <remarks>
/// The expiry cannot go in secrets.json, because encrypted dates break configuration binding.
/// It cannot go in netclaw.json, because the config watcher restarts the daemon on each
/// write to that file. The file has the shape <c>{ "Providers": { "&lt;name&gt;": { "OAuthTokenExpiry": "..." } } }</c>
/// so the configuration binder reads it with no extra code. A <c>null</c> value is kept on
/// purpose: it hides a retired <c>OAuthTokenExpiry</c> key that still sits in netclaw.json.
/// </remarks>
public static class OAuthStateStore
{
    private const string ProvidersKey = "Providers";
    private const string ExpiryKey = nameof(ProviderEntry.OAuthTokenExpiry);

    // Two providers can refresh at the same time. Each write reads the whole file first.
    private static readonly object WriteGate = new();

    /// <summary>
    /// True when the file has an entry for the provider. <paramref name="expiry"/> is null when the
    /// entry says that the token has no expiry.
    /// </summary>
    public static bool TryGetExpiry(NetclawPaths paths, string providerName, out DateTimeOffset? expiry)
    {
        expiry = null;
        lock (WriteGate)
        {
            if (Load(paths) is not { } root
                || root[ProvidersKey] is not JsonObject providers
                || FindKey(providers, providerName) is not { } key
                || providers[key] is not JsonObject provider
                || FindKey(provider, ExpiryKey) is not { } expiryKey)
            {
                return false;
            }

            expiry = ParseExpiry(paths, provider[expiryKey]);
            return true;
        }
    }

    public static void SetExpiry(NetclawPaths paths, string providerName, DateTimeOffset? expiry)
    {
        lock (WriteGate)
        {
            var root = Load(paths) ?? [];
            var providers = root[ProvidersKey] as JsonObject ?? [];
            root[ProvidersKey] = providers;

            var key = FindKey(providers, providerName) ?? providerName;
            var provider = providers[key] as JsonObject ?? [];
            providers[key] = provider;

            provider[FindKey(provider, ExpiryKey) ?? ExpiryKey] = expiry?.ToString("o", CultureInfo.InvariantCulture);
            Save(paths, root);
        }
    }

    public static void Remove(NetclawPaths paths, string providerName)
    {
        lock (WriteGate)
        {
            if (Load(paths) is not { } root
                || root[ProvidersKey] is not JsonObject providers
                || FindKey(providers, providerName) is not { } key)
            {
                return;
            }

            providers.Remove(key);
            Save(paths, root);
        }
    }

    public static void Rename(NetclawPaths paths, string oldName, string newName)
    {
        lock (WriteGate)
        {
            if (Load(paths) is not { } root
                || root[ProvidersKey] is not JsonObject providers
                || FindKey(providers, oldName) is not { } key)
            {
                return;
            }

            var entry = providers[key];
            providers.Remove(key);
            providers[newName] = entry;
            Save(paths, root);
        }
    }

    /// <summary>
    /// The providers whose expiry comes from the retired <c>OAuthTokenExpiry</c> key in netclaw.json,
    /// because oauth-state.json has no entry for them. The daemon logs these at startup.
    /// </summary>
    public static IReadOnlyList<string> FindRetiredExpiryInUse(NetclawPaths paths)
    {
        if (!File.Exists(paths.NetclawConfigPath))
            return [];

        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var providers = (JsonNode.Parse(File.ReadAllText(paths.NetclawConfigPath), documentOptions: options) as JsonObject)
            ?.FirstOrDefault(static property => string.Equals(property.Key, ProvidersKey, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
        if (providers is null)
            return [];

        return providers
            .Where(static provider => provider.Value is JsonObject entry
                && entry.Any(static property => string.Equals(property.Key, ExpiryKey, StringComparison.OrdinalIgnoreCase)
                    && property.Value is not null))
            .Select(static provider => provider.Key)
            .Where(name => !TryGetExpiry(paths, name, out _))
            .ToList();
    }

    // A missing file is a normal state: no provider has refreshed yet. A file that cannot be
    // read stops the caller, as a bad secrets.json does, so a lost expiry never goes unseen.
    private static JsonObject? Load(NetclawPaths paths)
    {
        if (!File.Exists(paths.OAuthStatePath))
            return null;

        try
        {
            return JsonNode.Parse(File.ReadAllText(paths.OAuthStatePath)) as JsonObject
                ?? throw new InvalidDataException("the top level is not a JSON object");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"Couldn't read oauth-state.json: {ex.Message}", ex);
        }
    }

    private static void Save(NetclawPaths paths, JsonObject root)
        => AtomicFile.WriteAllText(paths.OAuthStatePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private static DateTimeOffset? ParseExpiry(NetclawPaths paths, JsonNode? node)
    {
        if (node is null)
            return null;

        if (node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Couldn't read oauth-state.json: {ExpiryKey} is not a date ({paths.OAuthStatePath}).");
    }

    // The configuration binder reads keys without case, so a lookup does the same.
    private static string? FindKey(JsonObject parent, string name)
        => parent.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}
