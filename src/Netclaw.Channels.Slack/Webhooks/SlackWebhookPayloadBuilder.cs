// -----------------------------------------------------------------------
// <copyright file="SlackWebhookPayloadBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using System.Text.Json.Nodes;

namespace Netclaw.Channels.Slack.Webhooks;

/// <summary>
/// Builds Slack Block Kit payloads for incoming webhook delivery.
/// No SlackNet dependency. The JSON document has a stable shape for Native AOT.
/// </summary>
public static class SlackWebhookPayloadBuilder
{
    private static readonly string Hostname = Environment.MachineName;
    /// <summary>
    /// Build a Slack-compatible webhook payload with a required <c>text</c> fallback
    /// and a <c>blocks</c> array for rich formatting. <paramref name="identity"/> is
    /// the emitting netclaw instance — surfaced so alerts from multiple instances
    /// in a shared channel can be told apart.
    /// </summary>
    public static object Build(OperationalAlert alert, ServiceIdentity identity)
    {
        var emoji = SeverityEmoji(alert.Severity);
        var blocks = new JsonArray
        {
            (JsonNode)new JsonObject
            {
                ["type"] = "header",
                ["text"] = new JsonObject
                {
                    ["type"] = "plain_text", ["text"] = $"{emoji} {alert.Type}", ["emoji"] = true
                }
            },
            (JsonNode)new JsonObject
            {
                ["type"] = "section",
                ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = alert.Summary }
            },
            (JsonNode)new JsonObject
            {
                ["type"] = "section",
                ["fields"] = BuildFields(alert, identity)
            },
        };

        var elements = new JsonArray();
        if (identity.Namespace is not null)
            elements.Add((JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*namespace:* {identity.Namespace}" });
        if (identity.InstanceId is not null)
            elements.Add((JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*instance:* {identity.InstanceId}" });
        elements.Add((JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*version:* {identity.Version}" });
        if (alert.Context is { Count: > 0 })
        {
            foreach (var (key, value) in alert.Context)
                elements.Add((JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*{key}:* {value}" });
        }
        blocks.Add((JsonNode)new JsonObject { ["type"] = "context", ["elements"] = elements });

        return new JsonObject
        {
            ["text"] = $"{emoji} [{alert.Severity}] {alert.Type}: {alert.Summary}",
            ["blocks"] = blocks,
        };
    }

    private static JsonArray BuildFields(OperationalAlert alert, ServiceIdentity identity)
    {
        var fields = new JsonArray
        {
            (JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*Severity:*\n{alert.Severity}" },
            (JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*Type:*\n{alert.Type}" },
            (JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*Timestamp:*\n{alert.Timestamp:u}" },
            (JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*Service:*\n{identity.Name}" },
            (JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*Hostname:*\n{Hostname}" },
        };

        if (alert.Source is not null)
            fields.Add((JsonNode)new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*Source:*\n{alert.Source}" });

        return fields;
    }

    private static string SeverityEmoji(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => ":red_circle:",
        AlertSeverity.Warning => ":warning:",
        AlertSeverity.Info => ":information_source:",
        _ => ":grey_question:",
    };
}
