// -----------------------------------------------------------------------
// <copyright file="CliStringEnumJsonContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Cli.Json;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<PairedDevice>))]
[JsonSerializable(typeof(PairedDevice[]))]
[JsonSerializable(typeof(WebhookRouteConfig))]
[JsonSerializable(typeof(Dictionary<string, McpServerEntry>))]
[JsonSerializable(typeof(McpServerEntry))]
[JsonSerializable(typeof(ProviderEntry))]
[JsonSerializable(typeof(SecurityPolicyConfig))]
[JsonSerializable(typeof(ModelReference))]
[JsonSerializable(typeof(ExternalSkillsConfig))]
[JsonSerializable(typeof(NotificationsConfig))]
[JsonSerializable(typeof(SkillFeedsConfig))]
internal sealed partial class CliStringEnumJsonContext : JsonSerializerContext;
