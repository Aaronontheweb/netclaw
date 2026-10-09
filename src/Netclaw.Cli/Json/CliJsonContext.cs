// -----------------------------------------------------------------------
// <copyright file="CliJsonContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Netclaw.Actors.Reminders;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Reminder;
using Netclaw.Cli.Webhooks;
using Netclaw.Configuration;

namespace Netclaw.Cli.Json;

[JsonSerializable(typeof(LocalControlPairingCodeRequest))]
[JsonSerializable(typeof(PairingCodeResultDto))]
[JsonSerializable(typeof(PairingCodeErrorResponse))]
[JsonSerializable(typeof(DaemonRuntimeStatus.Response), TypeInfoPropertyName = "RuntimeStatusResponse")]
[JsonSerializable(typeof(DaemonRuntimeStatus.Process), TypeInfoPropertyName = "RuntimeStatusProcess")]
[JsonSerializable(typeof(DaemonRuntimeStatus.Memory), TypeInfoPropertyName = "RuntimeStatusMemory")]
[JsonSerializable(typeof(DaemonRuntimeStatus.Reminders), TypeInfoPropertyName = "RuntimeStatusReminders")]
[JsonSerializable(typeof(List<SessionCatalogEntryDto>))]
[JsonSerializable(typeof(DaemonStats.Response), TypeInfoPropertyName = "DaemonStatsResponse")]
[JsonSerializable(typeof(DaemonStats.Process), TypeInfoPropertyName = "DaemonStatsProcess")]
[JsonSerializable(typeof(DaemonStats.Memory), TypeInfoPropertyName = "DaemonStatsMemory")]
[JsonSerializable(typeof(DaemonStats.Reminders), TypeInfoPropertyName = "DaemonStatsReminders")]
[JsonSerializable(typeof(SkillUsageStats.Response), TypeInfoPropertyName = "SkillUsageStatsResponse")]
[JsonSerializable(typeof(SkillInventory.Response), TypeInfoPropertyName = "SkillInventoryResponse")]
[JsonSerializable(typeof(SkillSyncResult.Response), TypeInfoPropertyName = "SkillSyncResultResponse")]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<PairedDeviceInfoDto>))]
[JsonSerializable(typeof(ClientConfigFile))]
[JsonSerializable(typeof(ReminderCommand.ReminderListRow[]))]
[JsonSerializable(typeof(ReminderCommand.ReminderStatusView))]
[JsonSerializable(typeof(ReminderDefinition))]
[JsonSerializable(typeof(HistoryRecord[]))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(object[]))]
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
[JsonSerializable(typeof(HeadlessChannel.JsonEnvelope))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(PairCommand.PairExchangeRequest))]
[JsonSerializable(typeof(PairCommand.ExchangeResponse))]
[JsonSerializable(typeof(WebhookRoutePatch))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
    internal static readonly CliJsonContext Api = new(new JsonSerializerOptions(JsonDefaults.Api));

    internal static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerContext context)
        => (JsonTypeInfo<T>)(context.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"Missing JSON metadata for {typeof(T)}."));

    internal static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerOptions? options = null)
    {
        if (options is null)
            return TypeInfo<T>(Default);

        if (ReferenceEquals(options, JsonDefaults.ConfigFile)
            || ReferenceEquals(options, JsonDefaults.ConfigRead)
            || ReferenceEquals(options, JsonDefaults.EnumAware))
            return TypeInfo<T>(new CliStringEnumJsonContext(new JsonSerializerOptions(options)));

        return TypeInfo<T>(new CliJsonContext(new JsonSerializerOptions(options)));
    }
}
