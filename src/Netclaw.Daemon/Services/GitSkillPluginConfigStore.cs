// -----------------------------------------------------------------------
// <copyright file="GitSkillPluginConfigStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

internal enum GitSkillPluginConfigFailure
{
    Invalid,
    Conflict,
    NotFound,
}

internal sealed class GitSkillPluginConfigException(
    GitSkillPluginConfigFailure failure,
    string message) : Exception(message)
{
    public GitSkillPluginConfigFailure Failure { get; } = failure;
}

/// <summary>Owns atomic plugin source mutations in the daemon config file.</summary>
internal sealed class GitSkillPluginConfigStore(NetclawPaths paths)
{
    private static readonly JsonSerializerOptions ConfigOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();

    public void Add(GitSkillPluginSource source)
    {
        lock (_gate)
        {
            var root = LoadRoot();
            var plugins = LoadPlugins(root);
            if (plugins.Any(item => string.Equals(item.Name, source.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new GitSkillPluginConfigException(
                    GitSkillPluginConfigFailure.Conflict,
                    $"Plugin '{source.Name}' already exists.");
            }

            plugins.Add(source);
            WritePlugins(root, plugins);
        }
    }

    public bool SetEnabled(string name, bool enabled)
    {
        lock (_gate)
        {
            var root = LoadRoot();
            var plugins = LoadPlugins(root);
            var source = plugins.FirstOrDefault(
                item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                throw new GitSkillPluginConfigException(
                    GitSkillPluginConfigFailure.NotFound,
                    $"Plugin '{name}' was not found.");
            }

            if (source.Enabled == enabled)
                return false;

            source.Enabled = enabled;
            WritePlugins(root, plugins);
            return true;
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var root = LoadRoot();
            var plugins = LoadPlugins(root);
            var removed = plugins.RemoveAll(
                item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                throw new GitSkillPluginConfigException(
                    GitSkillPluginConfigFailure.NotFound,
                    $"Plugin '{name}' was not found.");
            }

            WritePlugins(root, plugins);
            return true;
        }
    }

    private JsonObject LoadRoot()
    {
        if (!File.Exists(paths.NetclawConfigPath))
        {
            return new JsonObject
            {
                ["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion,
            };
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(paths.NetclawConfigPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new GitSkillPluginConfigException(
                GitSkillPluginConfigFailure.Invalid,
                $"The daemon configuration could not be read: {ex.Message}");
        }

        if (node is not JsonObject root)
        {
            throw new GitSkillPluginConfigException(
                GitSkillPluginConfigFailure.Invalid,
                "The daemon configuration root must be an object.");
        }

        root["configVersion"] ??= EmbeddedSchemaLoader.CurrentSchemaVersion;
        return root;
    }

    private static List<GitSkillPluginSource> LoadPlugins(JsonObject root)
    {
        if (root["SkillFeeds"] is null)
            return [];
        if (root["SkillFeeds"] is not JsonObject feeds)
        {
            throw new GitSkillPluginConfigException(
                GitSkillPluginConfigFailure.Invalid,
                "SkillFeeds must be an object.");
        }
        if (feeds["Plugins"] is null)
            return [];

        try
        {
            return feeds["Plugins"]!.Deserialize<List<GitSkillPluginSource>>(ConfigOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new GitSkillPluginConfigException(
                GitSkillPluginConfigFailure.Invalid,
                $"SkillFeeds.Plugins is invalid: {ex.Message}");
        }
    }

    private void WritePlugins(JsonObject root, List<GitSkillPluginSource> plugins)
    {
        if (!GitSkillPluginSourceValidator.TryValidateSources(plugins, out var error))
        {
            throw new GitSkillPluginConfigException(
                GitSkillPluginConfigFailure.Invalid,
                error);
        }

        var feeds = root["SkillFeeds"] as JsonObject;
        if (feeds is null)
        {
            feeds = new JsonObject();
            root["SkillFeeds"] = feeds;
        }

        if (plugins.Count == 0)
            feeds.Remove("Plugins");
        else
            feeds["Plugins"] = JsonSerializer.SerializeToNode(plugins, ConfigOptions);

        try
        {
            AtomicFile.WriteAllText(paths.NetclawConfigPath, root.ToJsonString(ConfigOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GitSkillPluginConfigException(
                GitSkillPluginConfigFailure.Invalid,
                $"The daemon configuration could not be written: {ex.Message}");
        }
    }
}
