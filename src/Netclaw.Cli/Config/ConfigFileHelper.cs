// -----------------------------------------------------------------------
// <copyright file="ConfigFileHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Cli.Config;

/// <summary>
/// Shared helpers for reading and writing netclaw.json and secrets.json config files.
/// Extracted from McpCommand for reuse by ProviderCommand, ModelCommand, and TUI flows.
/// </summary>
internal static class ConfigFileHelper
{
    /// <summary>
    /// Load both netclaw.json and secrets.json as mutable dictionaries.
    /// Missing files get a default <c>{ "configVersion": 1 }</c> skeleton.
    /// </summary>
    internal static (Dictionary<string, object> config, Dictionary<string, object> secrets)
        LoadConfigFiles(Configuration.NetclawPaths paths)
    {
        var config = LoadJsonDict(paths.NetclawConfigPath);
        var secrets = LoadJsonDict(paths.SecretsPath);
        return (config, secrets);
    }

    /// <summary>
    /// Load a JSON file as a mutable dictionary. Returns a default skeleton if the file doesn't exist.
    /// Throws on malformed JSON — use <see cref="LoadJsonDictOrBackup"/> for save paths that
    /// need to recover from corruption.
    /// </summary>
    internal static Dictionary<string, object> LoadJsonDict(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, object> { ["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion };

        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(text)
            ?? new Dictionary<string, object> { ["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion };
    }

    /// <summary>
    /// Save-path variant of <see cref="LoadJsonDict"/> that handles a corrupt
    /// existing file by renaming it to <c>&lt;path&gt;.corrupt.&lt;timestamp&gt;</c>
    /// and starting fresh. Used by <see cref="Tui.Wizard.WizardConfigBuilder.WriteConfigFile"/>
    /// so init / config saves never throw when the on-disk file is unparseable.
    /// </summary>
    /// <remarks>
    /// <paramref name="timeProvider"/> SHOULD be the DI-injected provider in
    /// production paths; tests can pass <see cref="FakeTimeProvider"/> to
    /// assert on deterministic backup filenames. Defaults to
    /// <see cref="TimeProvider.System"/> only as a compatibility shim for
    /// call sites that don't yet carry a TimeProvider.
    /// </remarks>
    internal static Dictionary<string, object> LoadJsonDictOrBackup(
        string path, TimeProvider? timeProvider = null)
    {
        if (!File.Exists(path))
            return new Dictionary<string, object> { ["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion };

        try
        {
            var text = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(text);
            if (parsed is not null)
                return parsed;
        }
        catch (JsonException)
        {
            // Fall through to the backup path.
        }

        var clock = timeProvider ?? TimeProvider.System;
        var backupPath = $"{path}.corrupt.{clock.GetUtcNow():yyyyMMddHHmmss}";
        try
        {
            File.Move(path, backupPath, overwrite: false);
        }
        catch (IOException)
        {
            // If the backup already exists or move fails, fall back to delete.
            try { File.Delete(path); } catch { /* best-effort */ }
        }

        return new Dictionary<string, object> { ["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion };
    }

    /// <summary>
    /// Get or create a nested dictionary section. Handles JsonElement deserialization
    /// when the section was loaded from a file.
    /// </summary>
    internal static Dictionary<string, object> GetOrCreateSection(
        Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var existing) && existing is not null)
        {
            if (existing is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Null)
                {
                    var fresh = new Dictionary<string, object>();
                    dict[key] = fresh;
                    return fresh;
                }

                var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText())
                    ?? [];
                dict[key] = parsed;
                return parsed;
            }

            return (Dictionary<string, object>)existing;
        }

        var section = new Dictionary<string, object>();
        dict[key] = section;
        return section;
    }

    /// <summary>
    /// Get an existing nested dictionary section, or null if it doesn't exist.
    /// </summary>
    internal static Dictionary<string, object>? GetSectionOrNull(
        Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var existing))
            return null;

        if (existing is JsonElement je)
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText())
                ?? [];
            dict[key] = parsed;
            return parsed;
        }

        return existing as Dictionary<string, object>;
    }

    /// <summary>
    /// Serialize a config dictionary and write it to disk, creating parent directories if needed.
    /// </summary>
    internal static void WriteConfigFile(string path, Dictionary<string, object> data)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonDefaults.Indented));
    }

    /// <summary>
    /// Serialize and write secrets.json using hardened permissions and encryption-at-rest.
    /// </summary>
    internal static void WriteSecretsFile(Configuration.NetclawPaths paths, Dictionary<string, object> data)
    {
        var protector = SecretsProtection.CreateProtector(paths);
        SecretsFileWriter.Write(paths.SecretsPath, data, options: JsonDefaults.Indented, protector: protector);
    }

    internal static string DecryptIfEncrypted(Configuration.NetclawPaths paths, string? value)
    {
        if (string.IsNullOrEmpty(value) || !ISecretsProtector.IsEncrypted(value))
            return value ?? string.Empty;

        var protector = SecretsProtection.CreateProtector(paths);
        return protector.Unprotect(value);
    }

    /// <summary>
    /// Probe whether a secret exists at the given dotted path in <c>secrets.json</c>
    /// WITHOUT decrypting the stored value. Returns <c>true</c> when the path
    /// resolves to a non-empty string (encrypted or plaintext) or to any
    /// non-null non-string value. Returns <c>false</c> when the path is
    /// missing, resolves to a JSON null, or resolves to an empty string.
    /// </summary>
    /// <remarks>
    /// Used by leaf editors to choose between "configured — leave blank to
    /// keep" and "(not set)" hint text without ever materializing the
    /// decrypted value in the UI.
    /// </remarks>
    public static bool SecretPresent(Configuration.NetclawPaths paths, string dottedPath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(dottedPath);

        if (!File.Exists(paths.SecretsPath))
            return false;

        JsonNode? node;
        try
        {
            var text = File.ReadAllText(paths.SecretsPath);
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        foreach (var segment in dottedPath.Split('.'))
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(segment, out var child))
                node = child;
            else
                return false;
        }

        return node switch
        {
            null => false,
            JsonValue val when val.TryGetValue<string>(out var s) => IsPresentString(s),
            JsonValue => true,
            _ => true,
        };

        // ENC: prefix denotes an encrypted value at rest. Return false for
        // an ENC: prefix with empty ciphertext so the UI distinguishes
        // "configured" from "corrupt / re-enter required".
        static bool IsPresentString(string s) =>
            !string.IsNullOrEmpty(s)
            && (!ISecretsProtector.IsEncrypted(s)
                || s.Length > "ENC:".Length);
    }
}
