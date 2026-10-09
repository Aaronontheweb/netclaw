// -----------------------------------------------------------------------
// <copyright file="DaemonJsonContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Daemon.Mcp;
using Netclaw.Daemon.Reminders;
using Netclaw.Daemon.Security;
using Netclaw.Daemon.Webhooks;

namespace Netclaw.Daemon.Json;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<PairedDevice>))]
[JsonSerializable(typeof(WebhookRouteConfig))]
[JsonSerializable(typeof(McpOAuthTokenSet))]
[JsonSerializable(typeof(SkillSyncState))]
[JsonSerializable(typeof(RestartManifest))]
[JsonSerializable(typeof(MattermostActionEndpointExtensions.ActionCallbackPayload))]
[JsonSerializable(typeof(MattermostActionEndpointExtensions.ActionCallbackResponse))]
[JsonSerializable(typeof(SubscriptionsAcknowledgedNotificationParams))]
[JsonSerializable(typeof(WebhookNotificationService.WebhookPayload))]
[JsonSerializable(typeof(WebhookIgnoredResponse))]
[JsonSerializable(typeof(WebhookAcceptedResponse))]
[JsonSerializable(typeof(ReminderImportErrorResponse))]
[JsonSerializable(typeof(PairingErrorResponse))]
[JsonSerializable(typeof(McpErrorResponse))]
internal sealed partial class DaemonJsonContext : JsonSerializerContext
{
    internal static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    internal static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerOptions? options = null)
    {
        var context = options is null ? Default : new DaemonJsonContext(new JsonSerializerOptions(options));
        return (JsonTypeInfo<T>)(context.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"Missing JSON metadata for {typeof(T)}."));
    }
}
