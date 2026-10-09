// -----------------------------------------------------------------------
// <copyright file="CliJson.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netclaw.Cli.Json;

internal static class CliJson
{
    internal static T? Deserialize<T>(string json, JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize(json, CliJsonContext.TypeInfo<T>(options));

    internal static T? Deserialize<T>(JsonElement json, JsonSerializerOptions? options = null)
        => json.Deserialize(CliJsonContext.TypeInfo<T>(options));

    internal static T? Deserialize<T>(JsonNode? json, JsonSerializerOptions? options = null)
        => json is null ? default : json.Deserialize(CliJsonContext.TypeInfo<T>(options));

    internal static string Serialize<T>(T value, JsonSerializerOptions? options = null)
        => JsonSerializer.Serialize(value, CliJsonContext.TypeInfo<T>(options));

    internal static JsonNode? SerializeToNode<T>(T value, JsonSerializerOptions? options = null)
        => JsonSerializer.SerializeToNode(value, CliJsonContext.TypeInfo<T>(options));

    internal static JsonElement SerializeToElement<T>(T value, JsonSerializerOptions? options = null)
        => JsonSerializer.SerializeToElement(value, CliJsonContext.TypeInfo<T>(options));

    internal static byte[] SerializeToUtf8Bytes<T>(T value, JsonSerializerOptions? options = null)
        => JsonSerializer.SerializeToUtf8Bytes(value, CliJsonContext.TypeInfo<T>(options));
}
