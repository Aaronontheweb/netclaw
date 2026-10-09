// -----------------------------------------------------------------------
// <copyright file="ConfigurationJsonContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Configuration.Json;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(WebhookRouteConfig))]
[JsonSerializable(typeof(BootstrapStateRecord))]
[JsonSerializable(typeof(BinaryFeedManifest))]
[JsonSerializable(typeof(string))]
internal sealed partial class ConfigurationJsonContext : JsonSerializerContext
{
    internal static readonly ConfigurationJsonContext RouteStore = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    });

    internal static readonly ConfigurationJsonContext BootstrapState = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });
}
