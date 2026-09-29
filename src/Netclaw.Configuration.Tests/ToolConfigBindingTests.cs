// -----------------------------------------------------------------------
// <copyright file="ToolConfigBindingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Netclaw.Media;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Pins the daemon binding of the <c>Tools</c> section. The Microsoft configuration
/// binder adds configured items to a list that already has default items. These tests
/// prove that a configured list replaces the default list, so an operator can narrow
/// tool grants, read roots, attachment categories, and the HTTP allow list.
/// </summary>
public sealed class ToolConfigBindingTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Configured_lists_replace_the_default_lists()
    {
        var toolConfig = Bind(
            """
            {
              "Tools": {
                "AudienceProfiles": {
                  "Team": {
                    "ToolsMode": "Allowlist",
                    "AllowedTools": ["file_read", "file_list"],
                    "ReadFiles": { "Mode": "Roots", "Roots": ["/srv/docs"] },
                    "ChannelAttachments": { "AllowedCategories": ["Image"] }
                  },
                  "Public": {
                    "AllowedTools": ["file_read"]
                  },
                  "Personal": {
                    "ChannelAttachments": { "AllowedCategories": ["Pdf"] }
                  },
                  "GlobalReadRoots": ["{skills_dir}"]
                },
                "WebFetch": { "HttpAllowList": ["localhost"] }
              }
            }
            """);

        var profiles = toolConfig.AudienceProfiles;
        Assert.Multiple(
            () => Assert.Equal(["file_read", "file_list"], profiles.Team.AllowedTools),
            () => Assert.Equal(["/srv/docs"], profiles.Team.ReadFiles.Roots),
            () => Assert.Equal([AttachmentCategory.Image], profiles.Team.ChannelAttachments.AllowedCategories),
            () => Assert.Equal(["file_read"], profiles.Public.AllowedTools),
            () => Assert.Equal([AttachmentCategory.Pdf], profiles.Personal.ChannelAttachments.AllowedCategories),
            () => Assert.Equal([ToolAudienceProfileDefaults.SkillsDirectoryToken], profiles.GlobalReadRoots),
            () => Assert.Equal(["localhost"], toolConfig.WebFetch.HttpAllowList));
    }

    [Fact]
    public void Absent_keys_keep_the_default_lists()
    {
        // The Team section exists, but it has no list keys. Each default list must stay intact.
        var toolConfig = Bind(
            """
            {
              "Tools": {
                "AudienceProfiles": {
                  "Team": { "ToolsMode": "Allowlist" }
                },
                "WebFetch": { "RequireHttps": true }
              }
            }
            """);

        var defaults = new ToolConfig();
        var profiles = toolConfig.AudienceProfiles;
        Assert.Equal(defaults.AudienceProfiles.Team.AllowedTools, profiles.Team.AllowedTools);
        Assert.Equal(defaults.AudienceProfiles.Team.ReadFiles.Roots, profiles.Team.ReadFiles.Roots);
        Assert.Equal(
            defaults.AudienceProfiles.Team.ChannelAttachments.AllowedCategories,
            profiles.Team.ChannelAttachments.AllowedCategories);
        Assert.Equal(defaults.AudienceProfiles.Public.AllowedTools, profiles.Public.AllowedTools);
        Assert.Equal(defaults.AudienceProfiles.GlobalReadRoots, profiles.GlobalReadRoots);
        Assert.Equal(defaults.WebFetch.HttpAllowList, toolConfig.WebFetch.HttpAllowList);
    }

    [Fact]
    public void Missing_tools_section_binds_the_defaults()
    {
        var toolConfig = Bind("""{ "configVersion": 1 }""");

        var defaults = new ToolConfig();
        Assert.Equal(defaults.AudienceProfiles.Team.AllowedTools, toolConfig.AudienceProfiles.Team.AllowedTools);
        Assert.Equal(defaults.AudienceProfiles.GlobalReadRoots, toolConfig.AudienceProfiles.GlobalReadRoots);
        Assert.Equal(defaults.WebFetch.HttpAllowList, toolConfig.WebFetch.HttpAllowList);
    }

    [Fact]
    public void Explicit_empty_arrays_bind_empty_lists()
    {
        // The JSON provider stores an empty array as a key with an empty string value.
        // An absent key has no entry. The binder can tell the two cases apart.
        var toolConfig = Bind(
            """
            {
              "Tools": {
                "AudienceProfiles": {
                  "Team": { "AllowedTools": [] },
                  "Public": { "ChannelAttachments": { "AllowedCategories": [] } },
                  "GlobalReadRoots": []
                },
                "WebFetch": { "HttpAllowList": [] }
              }
            }
            """);

        var profiles = toolConfig.AudienceProfiles;
        Assert.Multiple(
            () => Assert.Empty(profiles.Team.AllowedTools),
            () => Assert.Empty(profiles.Public.ChannelAttachments.AllowedCategories),
            () => Assert.Empty(profiles.GlobalReadRoots),
            () => Assert.Empty(toolConfig.WebFetch.HttpAllowList));
    }

    [Fact]
    public void Empty_environment_variable_binds_an_empty_list()
    {
        // The daemon adds environment variables after the JSON files. An empty value is an
        // explicit empty list, the same as an empty JSON array.
        var prefix = $"NETCLAW_TEST_{Guid.NewGuid():N}_";
        var variable = prefix + "Tools__AudienceProfiles__GlobalReadRoots";
        Environment.SetEnvironmentVariable(variable, string.Empty);
        try
        {
            var toolConfig = Bind("""{ "configVersion": 1 }""", prefix);

            Assert.Empty(toolConfig.AudienceProfiles.GlobalReadRoots);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Scalar_value_for_a_list_fails_loudly()
    {
        // Before this fix, the binder ignored a scalar value for a list and kept the defaults.
        // A misconfigured grant list must stop startup, not silently keep the wide defaults.
        var ex = Assert.Throws<InvalidOperationException>(() => Bind(
            """
            {
              "Tools": {
                "AudienceProfiles": {
                  "Team": { "AllowedTools": "file_read" }
                }
              }
            }
            """));

        Assert.Contains("Tools.AudienceProfiles.Team.AllowedTools", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void Json_null_or_empty_object_for_a_list_binds_an_empty_list_with_a_warning(string value)
    {
        // Owner decision: null and {} mean an empty list. Every list with default items is an
        // allow list, so an empty list grants less. The daemon logs each warning at startup.
        var toolConfig = Bind(
            $$"""
            {
              "Tools": {
                "AudienceProfiles": {
                  "Team": { "AllowedTools": {{value}} },
                  "GlobalReadRoots": {{value}}
                }
              }
            }
            """,
            out var warnings);

        Assert.Empty(toolConfig.AudienceProfiles.Team.AllowedTools);
        Assert.Empty(toolConfig.AudienceProfiles.GlobalReadRoots);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(
            "Tools.AudienceProfiles.Team.AllowedTools is null or an empty object; treating it as an empty list.",
            warnings);
        Assert.Contains(
            "Tools.AudienceProfiles.GlobalReadRoots is null or an empty object; treating it as an empty list.",
            warnings);
    }

    [Fact]
    public void Configured_lists_produce_no_warnings()
    {
        Bind(
            """
            {
              "Tools": {
                "AudienceProfiles": { "Team": { "AllowedTools": ["file_read"] }, "GlobalReadRoots": [] }
              }
            }
            """,
            out var warnings);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Empty_environment_variable_and_json_items_fail_loudly()
    {
        // IConfiguration merges sources, so the JSON items would win over the empty variable.
        // The operator asked for an empty list, so a silent non-empty list is a misconfiguration.
        var prefix = $"NETCLAW_TEST_{Guid.NewGuid():N}_";
        var variable = prefix + "Tools__AudienceProfiles__Team__AllowedTools";
        Environment.SetEnvironmentVariable(variable, string.Empty);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Bind(
                """
                {
                  "Tools": {
                    "AudienceProfiles": { "Team": { "AllowedTools": ["file_read", "file_list"] } }
                  }
                }
                """,
                out _,
                prefix));

            Assert.Contains("Tools.AudienceProfiles.Team.AllowedTools", ex.Message, StringComparison.Ordinal);
            Assert.Contains("list items and also an empty or scalar value", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("99")]
    public void Unknown_enum_item_fails_loudly(string category)
    {
        // The Microsoft binder drops an item that it cannot convert, which turned ["Bogus"] into [].
        var ex = Assert.Throws<InvalidOperationException>(() => Bind(
            $$"""
            {
              "Tools": {
                "AudienceProfiles": {
                  "Team": { "ChannelAttachments": { "AllowedCategories": ["Image", "{{category}}"] } }
                }
              }
            }
            """));

        Assert.Contains("Tools.AudienceProfiles.Team.ChannelAttachments.AllowedCategories.1", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"'{category}'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Narrowed_attachment_categories_pass_startup_validation()
    {
        // The TUI writes "None" as an empty category list with zero caps. Before this fix, the
        // binder kept the default categories, so startup validation rejected the zero caps.
        var toolConfig = Bind(
            """
            {
              "Tools": {
                "AudienceProfiles": {
                  "Team": {
                    "ChannelAttachments": { "AllowedCategories": [], "MaxFileBytes": 0, "MaxFilesPerMessage": 0 }
                  }
                }
              }
            }
            """);

        Assert.Empty(toolConfig.AudienceProfiles.Team.ChannelAttachments.AllowedCategories);
    }

    [Theory]
    [InlineData(typeof(SecurityPolicyConfig))]
    [InlineData(typeof(WebhooksConfig))]
    [InlineData(typeof(SearchConfig))]
    [InlineData(typeof(SubAgentConfig))]
    [InlineData(typeof(MemoryConfig))]
    [InlineData(typeof(SkillSyncConfig))]
    [InlineData(typeof(SchedulingConfig))]
    [InlineData(typeof(ExternalSkillsConfig))]
    [InlineData(typeof(SkillFeedsConfig))]
    [InlineData(typeof(NotificationsConfig))]
    [InlineData(typeof(McpServerEntry))]
    public void Config_types_bound_without_the_list_binder_have_no_default_list_items(Type configType)
    {
        // The daemon binds these types with the plain Microsoft binder. That is safe only while
        // each default list is empty. A default list item needs ConfigurationListBinder and a
        // new decision about what null and {} mean for that list.
        var findings = new List<string>();
        WalkConfigGraph(Activator.CreateInstance(configType)!, configType.Name, findings, checkReplaceable: false);

        Assert.Empty(findings);
    }

    [Fact]
    public void Tool_config_lists_with_default_items_are_the_reviewed_allow_lists()
    {
        // ConfigurationListBinder turns null and {} into an empty list. That is safe only for
        // allow lists. A new list with default items must be reviewed and added here.
        var findings = new List<string>();
        WalkConfigGraph(new ToolConfig(), nameof(ToolConfig), findings, checkReplaceable: true);

        Assert.Equal(
        [
            "ToolConfig.AudienceProfiles.Public.AllowedTools",
            "ToolConfig.AudienceProfiles.Public.ReadFiles.Roots",
            "ToolConfig.AudienceProfiles.Public.WriteFiles.Roots",
            "ToolConfig.AudienceProfiles.Public.AttachFiles.Roots",
            "ToolConfig.AudienceProfiles.Public.ChannelAttachments.AllowedCategories",
            "ToolConfig.AudienceProfiles.Team.AllowedTools",
            "ToolConfig.AudienceProfiles.Team.ReadFiles.Roots",
            "ToolConfig.AudienceProfiles.Team.WriteFiles.Roots",
            "ToolConfig.AudienceProfiles.Team.AttachFiles.Roots",
            "ToolConfig.AudienceProfiles.Team.ChannelAttachments.AllowedCategories",
            "ToolConfig.AudienceProfiles.Personal.ChannelAttachments.AllowedCategories",
            "ToolConfig.AudienceProfiles.GlobalReadRoots",
            "ToolConfig.WebFetch.HttpAllowList"
        ], findings);
    }

    // Records each list with default items. With checkReplaceable, it also records each list
    // that ConfigurationListBinder cannot replace, so the guard fails before startup does.
    private static void WalkConfigGraph(object target, string path, List<string> findings, bool checkReplaceable)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var propertyPath = $"{path}.{property.Name}";
            var propertyType = property.PropertyType;
            var value = property.GetValue(target);
            if (ConfigurationListBinder.IsList(propertyType))
            {
                if (checkReplaceable && property.SetMethod?.IsPublic != true)
                    findings.Add($"{propertyPath} (no public setter)");
                if (value is IEnumerable items && items.Cast<object>().Any())
                    findings.Add(propertyPath);
            }
            else if (ConfigurationListBinder.IsDictionary(propertyType))
            {
                if (value is IEnumerable entries && entries.Cast<object>().Any())
                    findings.Add(propertyPath);
            }
            else if (value is not null && ConfigurationListBinder.IsNestedObject(propertyType))
            {
                WalkConfigGraph(value, propertyPath, findings, checkReplaceable);
            }
        }
    }

    private ToolConfig Bind(string netclawJson, string environmentPrefix = "NETCLAW_TEST_UNUSED_")
        => Bind(netclawJson, out _, environmentPrefix);

    private ToolConfig Bind(
        string netclawJson,
        out IReadOnlyList<string> warnings,
        string environmentPrefix = "NETCLAW_TEST_UNUSED_")
    {
        var configPath = Path.Combine(_dir.Path, "netclaw.json");
        File.WriteAllText(configPath, netclawJson);

        // Same provider chain as the daemon: netclaw.json, secrets.json, then environment variables.
        // Daemon.Tests covers the daemon chain itself. This copy lets a test use a unique
        // environment prefix, so parallel tests do not share process environment variables.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true, reloadOnChange: false)
            .AddJsonFile(Path.Combine(_dir.Path, "secrets.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(environmentPrefix)
            .Build();

        return ToolConfig.BindFromConfiguration(configuration.GetSection("Tools"), out warnings);
    }
}
