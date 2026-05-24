// -----------------------------------------------------------------------
// <copyright file="SemanticConfigMergerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Config;
using Xunit;

namespace Netclaw.Cli.Tests.Config;

public sealed class SemanticConfigMergerTests
{
    [Fact]
    public void Merge_PreservesUnrelatedSections()
    {
        var existing = new Dictionary<string, object>
        {
            ["Slack"] = new Dictionary<string, object> { ["Enabled"] = true },
            ["Search"] = new Dictionary<string, object> { ["Backend"] = "duckduckgo" },
        };
        var newPartial = new Dictionary<string, object>
        {
            ["Search"] = new Dictionary<string, object> { ["Backend"] = "searxng" },
        };

        var merged = SemanticConfigMerger.Merge(existing, newPartial);

        Assert.True(merged.ContainsKey("Slack"));
        Assert.Equal("searxng", AsString(merged, "Search", "Backend"));
        Assert.True(AsBool(merged, "Slack", "Enabled"));
    }

    [Fact]
    public void Merge_DeepMerge_PreservesInactiveSubKeys()
    {
        // Inactive exposure-mode field preservation: existing has Daemon
        // with Host & TrustedProxies. New partial only updates ExposureMode.
        // Deep merge SHALL preserve the inactive Host and TrustedProxies.
        var existing = new Dictionary<string, object>
        {
            ["Daemon"] = new Dictionary<string, object>
            {
                ["ExposureMode"] = "ReverseProxy",
                ["Host"] = "0.0.0.0",
                ["TrustedProxies"] = new[] { "10.0.0.0/8" },
            },
        };
        var newPartial = new Dictionary<string, object>
        {
            ["Daemon"] = new Dictionary<string, object>
            {
                ["ExposureMode"] = "Public",
            },
        };

        var merged = SemanticConfigMerger.Merge(existing, newPartial);

        Assert.Equal("Public", AsString(merged, "Daemon", "ExposureMode"));
        Assert.Equal("0.0.0.0", AsString(merged, "Daemon", "Host"));
        Assert.NotNull(GetValue(merged, "Daemon", "TrustedProxies"));
    }

    [Fact]
    public void Merge_NewValueWinsOnTypeMismatch()
    {
        var existing = new Dictionary<string, object>
        {
            ["Search"] = new Dictionary<string, object> { ["Backend"] = "duckduckgo" },
        };
        var newPartial = new Dictionary<string, object>
        {
            ["Search"] = "disabled",  // string replaces nested object
        };

        var merged = SemanticConfigMerger.Merge(existing, newPartial);

        Assert.Equal(JsonValueKind.String, ((JsonElement)merged["Search"]).ValueKind);
    }

    [Fact]
    public void Merge_ArrayReplacesArray_NoElementMerge()
    {
        var existing = new Dictionary<string, object>
        {
            ["AllowedChannels"] = new[] { "C1", "C2", "C3" },
        };
        var newPartial = new Dictionary<string, object>
        {
            ["AllowedChannels"] = new[] { "C9" },
        };

        var merged = SemanticConfigMerger.Merge(existing, newPartial);

        var arr = (JsonElement)merged["AllowedChannels"];
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("C9", arr[0].GetString());
    }

    [Fact]
    public void Merge_HandlesJsonElementExisting()
    {
        // Simulates state loaded from disk via JsonSerializer where values
        // arrive as JsonElement rather than nested Dictionary<string, object>.
        var existingJson = """{"Search":{"Backend":"duckduckgo","Active":true}}""";
        var existing = JsonSerializer.Deserialize<Dictionary<string, object>>(existingJson)!;
        var newPartial = new Dictionary<string, object>
        {
            ["Search"] = new Dictionary<string, object> { ["Backend"] = "searxng" },
        };

        var merged = SemanticConfigMerger.Merge(existing, newPartial);

        Assert.Equal("searxng", AsString(merged, "Search", "Backend"));
        Assert.True(AsBool(merged, "Search", "Active"));
    }

    [Fact]
    public void Merge_NullInputs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SemanticConfigMerger.Merge(null!, new Dictionary<string, object>()));
        Assert.Throws<ArgumentNullException>(() =>
            SemanticConfigMerger.Merge(new Dictionary<string, object>(), null!));
    }

    [Fact]
    public void Merge_DoesNotMutateInputs()
    {
        var existing = new Dictionary<string, object>
        {
            ["Search"] = new Dictionary<string, object> { ["Backend"] = "duckduckgo" },
        };
        var newPartial = new Dictionary<string, object>
        {
            ["Search"] = new Dictionary<string, object> { ["Backend"] = "searxng" },
        };

        _ = SemanticConfigMerger.Merge(existing, newPartial);

        Assert.Equal("duckduckgo",
            ((Dictionary<string, object>)existing["Search"])["Backend"]);
        Assert.Equal("searxng",
            ((Dictionary<string, object>)newPartial["Search"])["Backend"]);
    }

    private static object? GetValue(Dictionary<string, object> dict, params string[] path)
    {
        object? cur = dict;
        foreach (var seg in path)
        {
            cur = cur switch
            {
                Dictionary<string, object> d when d.TryGetValue(seg, out var v) => v,
                JsonElement je when je.ValueKind == JsonValueKind.Object && je.TryGetProperty(seg, out var prop) => prop,
                _ => null,
            };
            if (cur is null) return null;
        }
        return cur;
    }

    private static string? AsString(Dictionary<string, object> dict, params string[] path) =>
        GetValue(dict, path) switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => null,
        };

    private static bool AsBool(Dictionary<string, object> dict, params string[] path) =>
        GetValue(dict, path) switch
        {
            bool b => b,
            JsonElement je => je.ValueKind == JsonValueKind.True,
            _ => false,
        };
}
