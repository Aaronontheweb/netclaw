// -----------------------------------------------------------------------
// <copyright file="ProviderEntryVendorOptionsExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration.Providers;

/// <summary>
/// Deserializes the opaque vendor-options bag into a provider-owned typed view.
/// </summary>
public static class ProviderEntryVendorOptionsExtensions
{
    // UnmappedMemberHandling.Disallow makes operator typos throw loudly instead
    // of silently producing the typed view's defaults. A misspelled key in
    // VendorOptions previously round-tripped to "default values" with no
    // signal — which on a security-shaped knob (e.g. Venice's system-prompt
    // override opt-in) means the operator's intent silently doesn't take
    // effect. No silent fallbacks: fail at config load and tell the operator
    // which key they fat-fingered.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static T? GetVendorOptions<T>(this ProviderEntry entry)
        where T : class, IVendorOptions
    {
        if (entry.VendorOptions is null)
            return null;

        try
        {
            return entry.VendorOptions.Deserialize<T>(SerializerOptions)
                ?? throw new InvalidOperationException(
                    $"Providers:<name>:VendorOptions could not be bound as {typeof(T).Name}.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Providers:<name>:VendorOptions is invalid for provider type '{entry.Type}' and options type '{typeof(T).Name}'.",
                ex);
        }
    }
}
