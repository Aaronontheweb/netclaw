// -----------------------------------------------------------------------
// <copyright file="EmbeddedSchemaLoader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Loads JSON schema files embedded in the Netclaw.Configuration assembly.
/// </summary>
public static class EmbeddedSchemaLoader
{
    private const string ResourcePrefix = "Netclaw.Configuration.Schemas.";

    /// <summary>
    /// Reads the embedded config schema for the given version. Returns null if not found.
    /// </summary>
    public static string? LoadConfigSchema(int version)
    {
        var resourceName = $"{ResourcePrefix}netclaw-config.v{version}.schema.json";
        return ReadResource(resourceName);
    }

    private static string? ReadResource(string resourceName)
    {
        using var stream = typeof(EmbeddedSchemaLoader).Assembly
            .GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
