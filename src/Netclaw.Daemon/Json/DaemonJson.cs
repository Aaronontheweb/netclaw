// -----------------------------------------------------------------------
// <copyright file="DaemonJson.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netclaw.Daemon.Json;

internal static class DaemonJson
{
    internal static T? Deserialize<T>(string json, JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize(json, DaemonJsonContext.TypeInfo<T>(options));

    internal static T? Deserialize<T>(byte[] json, JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize(json, DaemonJsonContext.TypeInfo<T>(options));

    internal static T? Deserialize<T>(Stream json, JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize(json, DaemonJsonContext.TypeInfo<T>(options));

    internal static string Serialize<T>(T value, JsonSerializerOptions? options = null)
        => JsonSerializer.Serialize(value, DaemonJsonContext.TypeInfo<T>(options));

    internal static JsonNode? SerializeToNode<T>(T value, JsonSerializerOptions? options = null)
        => JsonSerializer.SerializeToNode(value, DaemonJsonContext.TypeInfo<T>(options));
}
