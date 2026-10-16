// -----------------------------------------------------------------------
// <copyright file="DaemonEnvKeyPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Decides which <c>NETCLAW_*</c> keys <c>netclaw daemon install --env</c> may write to the plain-text
/// <c>daemon.env</c>. The embedded config schema is the allow-list. A key passes only when its path
/// names a property that the schema declares. The rule never judges a name that the operator chooses,
/// because it cannot tell a credential from a setting there.
/// </summary>
/// <remarks>
/// Positive: <c>NETCLAW_Providers__main__Type</c> passes. <c>main</c> is a provider name and matches as
/// a wildcard, and <c>Type</c> is declared. Negative: <c>NETCLAW_McpServers__db__EnvironmentVariables__SMTP_PASS</c>
/// fails. <c>EnvironmentVariables</c> is a free-form map, so the schema gives no meaning to <c>SMTP_PASS</c>.
/// A declared property that holds a credential, such as <c>ApiKey</c>, fails by the same rule that
/// <c>doctor --fix</c> uses to keep a credential out of a backup.
/// </remarks>
internal static class DaemonEnvKeyPolicy
{
    private const string KeyPrefix = "NETCLAW_";

    // Two variables that the process reads by name, outside the config schema. Both hold a path or a URL.
    private static readonly HashSet<string> ProcessSettings =
        new(["NETCLAW_HOME", "NETCLAW_DAEMON_ENDPOINT"], StringComparer.Ordinal);

    private static readonly Lazy<JsonObject> Schema = new(LoadSchema);

    /// <summary>
    /// Returns false and a reason when <paramref name="key"/> or <paramref name="value"/> must not go to
    /// daemon.env. The reason names the key and never holds the value.
    /// </summary>
    internal static bool TryAccept(string key, string value, out string reason)
    {
        reason = string.Empty;
        if (ProcessSettings.Contains(key))
            return AcceptUnlessUrlHoldsCredentials(key, value, out reason);

        var segments = key[KeyPrefix.Length..].Split("__");
        var schema = Schema.Value;
        var resolution = Resolve(schema, schema, segments, section: string.Empty);

        switch (resolution.Outcome)
        {
            case Outcome.Undeclared:
                reason = $"The --env key '{key}' is not a setting that the netclaw.json schema declares. "
                    + "Check the spelling, or set the value in netclaw.json. Store a credential with `netclaw secrets set`.";
                return false;
            case Outcome.FreeForm:
                reason = $"The --env key '{key}' is inside the free-form section '{resolution.FreeFormSection}'. "
                    + "The schema gives no meaning to a name there, and daemon.env is plain text, so netclaw cannot tell a credential from a setting. "
                    + "Set the value in netclaw.json, or store a credential with `netclaw secrets set`.";
                return false;
            case Outcome.Section:
                reason = $"The --env key '{key}' names a section, not a setting. Name a property inside it, or set the value in netclaw.json. "
                    + "Store a credential with `netclaw secrets set`.";
                return false;
        }

        var node = JsonValue.Create(value);
        foreach (var (name, declared) in resolution.DeclaredPath)
        {
            if (CanHoldText(declared) && SchemaFixResolver.HoldsCredential(name, node))
            {
                reason = $"Refusing to write '{key}' to daemon.env. The property '{name}' holds a credential, "
                    + "and daemon.env is plain text. Store the credential with `netclaw secrets set`.";
                return false;
            }
        }

        return AcceptUnlessUrlHoldsCredentials(key, value, out reason);
    }

    // A URL with a user name or a password is a credential whatever the property is called.
    private static bool AcceptUnlessUrlHoldsCredentials(string key, string value, out string reason)
    {
        if (SchemaFixResolver.HasUrlUserInfo(value))
        {
            reason = $"Refusing to write '{key}' to daemon.env. The value is a URL that holds a credential, "
                + "and daemon.env is plain text. Store the credential with `netclaw secrets set`.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private enum Outcome
    {
        Declared,
        Undeclared,
        FreeForm,
        Section,
    }

    private readonly record struct Resolution(
        Outcome Outcome,
        IReadOnlyList<(string Name, JsonObject Schema)> DeclaredPath,
        string FreeFormSection)
    {
        internal static Resolution Declared => new(Outcome.Declared, [], string.Empty);

        internal static Resolution Undeclared => new(Outcome.Undeclared, [], string.Empty);

        internal static Resolution NamesSection => new(Outcome.Section, [], string.Empty);

        internal static Resolution FreeFormIn(string section) => new(Outcome.FreeForm, [], section);
    }

    /// <summary>Walks the path through the schema. Names match without case, as the configuration binder does.</summary>
    private static Resolution Resolve(JsonObject root, JsonObject node, ReadOnlySpan<string> segments, string section)
    {
        node = Follow(root, node);

        // Models accepts two shapes. A key passes when any shape declares it.
        if (Alternatives(node) is { } alternatives)
        {
            var best = Resolution.Undeclared;
            foreach (var alternative in alternatives)
            {
                if (alternative is not JsonObject shape)
                    continue;

                var resolution = Resolve(root, shape, segments, section);
                if (resolution.Outcome == Outcome.Declared)
                    return resolution;
                if (resolution.Outcome != Outcome.Undeclared)
                    best = resolution;
            }

            return best;
        }

        if (segments.IsEmpty)
            return IsSection(node) ? Resolution.NamesSection : Resolution.Declared;

        var segment = segments[0];
        var rest = segments[1..];

        if (node["properties"] is JsonObject properties && FindIgnoreCase(properties, segment) is { } propertyName
            && properties[propertyName] is JsonObject property)
        {
            var child = Follow(root, property);
            var resolution = Resolve(root, child, rest, propertyName);
            return resolution.Outcome == Outcome.Declared
                ? resolution with { DeclaredPath = [(propertyName, child), .. resolution.DeclaredPath] }
                : resolution;
        }

        if (node["additionalProperties"] is JsonObject entrySchema)
        {
            var entry = Follow(root, entrySchema);

            // A keyed map of records (providers, MCP servers): the name is a wildcard and a declared property follows.
            if (entry["properties"] is JsonObject || Alternatives(entry) is not null)
                return Resolve(root, entry, rest, section);

            // A keyed map of a fixed choice (a channel id mapped to an audience) holds a setting under a wildcard name.
            if (rest.IsEmpty && IsFixedChoice(entry))
                return Resolution.Declared;

            return Resolution.FreeFormIn(section);
        }

        if (node["additionalProperties"] is JsonValue open && open.TryGetValue<bool>(out var anything) && anything)
            return Resolution.FreeFormIn(section);

        if (node["items"] is JsonObject items && IsIndex(segment))
            return Resolve(root, Follow(root, items), rest, section);

        return Resolution.Undeclared;
    }

    private static JsonArray? Alternatives(JsonObject node)
        => node["oneOf"] as JsonArray ?? node["anyOf"] as JsonArray;

    // A property that has members is a section. A leaf is a scalar or a list of scalars.
    private static bool IsSection(JsonObject node)
        => node["properties"] is JsonObject
           || node["additionalProperties"] is JsonObject
           || (node["additionalProperties"] is JsonValue open && open.TryGetValue<bool>(out var anything) && anything);

    private static bool IsFixedChoice(JsonObject node)
        => node["enum"] is JsonArray || TypeNames(node).All(type => type is "boolean" or "integer" or "number" or "null");

    private static bool CanHoldText(JsonObject node)
    {
        var types = TypeNames(node).ToList();
        return types.Count == 0 || types.Any(type => type is "string" or "object" or "array");
    }

    private static IEnumerable<string> TypeNames(JsonObject node)
        => node["type"] switch
        {
            JsonValue single when single.TryGetValue<string>(out var name) => [name],
            JsonArray many => many.Select(item => item?.GetValue<string>()).OfType<string>(),
            _ => [],
        };

    private static bool IsIndex(string segment)
        => segment.Length > 0 && segment.All(char.IsAsciiDigit);

    private static string? FindIgnoreCase(JsonObject properties, string name)
        => properties.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    private static JsonObject Follow(JsonObject root, JsonObject node)
    {
        while (node["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var pointer))
        {
            const string definitions = "#/$defs/";
            if (!pointer.StartsWith(definitions, StringComparison.Ordinal)
                || root["$defs"] is not JsonObject defs
                || defs[pointer[definitions.Length..]] is not JsonObject target)
            {
                throw new InvalidOperationException($"The embedded config schema holds a reference that this check cannot follow: {pointer}");
            }

            node = target;
        }

        return node;
    }

    private static JsonObject LoadSchema()
    {
        var text = EmbeddedSchemaLoader.LoadConfigSchema(EmbeddedSchemaLoader.CurrentSchemaVersion)
            ?? throw new InvalidOperationException("The embedded config schema is missing, so netclaw cannot check the --env keys.");
        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidOperationException("The embedded config schema is not a JSON object.");
    }
}
