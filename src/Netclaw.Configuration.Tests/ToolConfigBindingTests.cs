// -----------------------------------------------------------------------
// <copyright file="ToolConfigBindingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections;
using System.ComponentModel;
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

        Assert.Contains("Tools:AudienceProfiles:Team:AllowedTools", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_null_for_a_list_keeps_the_default_list()
    {
        // Documents current behavior. The JSON provider stores null as a key with a null
        // value, which IConfiguration treats as absent. The schema rejects null for these
        // arrays, so `netclaw doctor` reports it.
        var toolConfig = Bind(
            """
            {
              "Tools": {
                "AudienceProfiles": { "GlobalReadRoots": null }
              }
            }
            """);

        Assert.Equal(new ToolConfig().AudienceProfiles.GlobalReadRoots, toolConfig.AudienceProfiles.GlobalReadRoots);
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
        // each default list is empty. A default list item needs ConfigurationListBinder.
        var itemsByPath = new List<string>();
        CollectDefaultListItems(Activator.CreateInstance(configType)!, configType.Name, itemsByPath);

        Assert.Empty(itemsByPath);
    }

    private static void CollectDefaultListItems(object target, string path, List<string> itemsByPath)
    {
        foreach (var property in target.GetType().GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetValue(target) is not { } value)
                continue;

            var propertyPath = $"{path}.{property.Name}";
            if (value is IEnumerable items and not string)
            {
                if (items.Cast<object>().Any())
                    itemsByPath.Add(propertyPath);
            }
            else if (property.PropertyType.IsClass
                     && property.PropertyType != typeof(string)
                     && !TypeDescriptor.GetConverter(property.PropertyType)
                         .CanConvertFrom(typeof(string)))
            {
                CollectDefaultListItems(value, propertyPath, itemsByPath);
            }
        }
    }

    private ToolConfig Bind(string netclawJson, string environmentPrefix = "NETCLAW_TEST_UNUSED_")
    {
        var configPath = Path.Combine(_dir.Path, "netclaw.json");
        File.WriteAllText(configPath, netclawJson);

        // Same provider chain as the daemon: netclaw.json, secrets.json, then environment variables.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true, reloadOnChange: false)
            .AddJsonFile(Path.Combine(_dir.Path, "secrets.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(environmentPrefix)
            .Build();

        return ToolConfig.BindFromConfiguration(configuration.GetSection("Tools"));
    }
}
