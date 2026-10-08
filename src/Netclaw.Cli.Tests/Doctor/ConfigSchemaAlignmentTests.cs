// -----------------------------------------------------------------------
// <copyright file="ConfigSchemaAlignmentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Json.Schema;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

/// <summary>
/// The options classes are the source of truth for netclaw.json. Each one declares the section it
/// binds as <c>SectionName</c> (or <c>EntriesSectionName</c> for a dictionary of entries). This test
/// finds those classes by reflection, synthesizes the keys, JSON types, enum values and defaults they
/// bind, and diffs that against the embedded schema that <c>netclaw doctor</c> and
/// <c>doctor --fix</c> use. Nothing here is listed by hand. A key a class defines but the schema
/// lacks is reported with the schema entry to add. A key the schema has but no class defines is
/// reported with the entry to remove.
/// </summary>
public sealed class ConfigSchemaAlignmentTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed record Bound(string Section, Type Type, bool Entries);

    private sealed record Key(Type? Leaf, JsonNode? Default);

    // ---- the options classes ----------------------------------------------------------

    // Every class in the Netclaw assemblies that declares the section it binds. Loading the files
    // from the output folder picks up an options class in a new project without editing this test.
    private static readonly Lazy<List<Bound>> BoundLazy = new(() =>
        Directory.GetFiles(AppContext.BaseDirectory, "Netclaw.*.dll")
            .Where(file => !Path.GetFileName(file).Contains("Tests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom)
            .SelectMany(assembly => assembly.GetExportedTypes())
            .SelectMany(type => new[] { ("SectionName", false), ("EntriesSectionName", true) }
                .Select(c => (Field: type.GetField(c.Item1, BindingFlags.Public | BindingFlags.Static), Type: type, Entries: c.Item2)))
            .Where(c => c.Field is { IsLiteral: true })
            .Select(c => new Bound((string)c.Field!.GetRawConstantValue()!, c.Type, c.Entries))
            .OrderBy(b => b.Section, StringComparer.Ordinal)
            .ToList());

    private static List<Bound> Sections => BoundLazy.Value;

    // A class whose static BindFromConfiguration(IConfigurationSection) reads the keys by hand.
    private static MethodInfo? HandBinder(Type type)
        => type.GetMethod("BindFromConfiguration", BindingFlags.Public | BindingFlags.Static, [typeof(IConfigurationSection)]);

    // Shape: every path the options classes bind. Samples: per class, a config section that sets every
    // key to a value different from its default.
    private static readonly Lazy<(Dictionary<string, Key> Shape, Dictionary<string, JsonNode> Samples)> ModelLazy = new(BuildModel);

    private static Dictionary<string, Key> Shape => ModelLazy.Value.Shape;

    private static Dictionary<string, JsonNode> Samples => ModelLazy.Value.Samples;

    private static (Dictionary<string, Key>, Dictionary<string, JsonNode>) BuildModel()
    {
        var shape = new Dictionary<string, Key>(StringComparer.Ordinal);
        var samples = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var bound in Sections)
        {
            var path = bound.Entries ? $"{bound.Section}.*" : bound.Section;
            var binder = HandBinder(bound.Type);
            var effective = binder?.Invoke(null, [new ConfigurationBuilder().Build().GetSection(bound.Section)]);
            var shapeType = bound.Type.GetNestedType("Raw" + bound.Type.Name, BindingFlags.NonPublic) ?? bound.Type;
            var sample = Describe(shape, path, shapeType, effective ?? Create(shapeType), 0, bound.Type);
            if (binder is not null)
                AddRootAliases(shape, bound, sample);

            var key = bound.Entries ? new JsonObject { ["sample"] = sample } : sample;
            samples[$"{bound.Section}|{bound.Type.FullName}"] = key!;
            shape.TryAdd(bound.Section, new Key(null, null));
        }

        return (shape, samples);
    }

    // The binder fills public settable properties (an internal setter counts: ProviderConfigurationLoader
    // fills VendorOptions by hand) and get-only properties that already hold a collection or object.
    // [ConfigurationKeyName] renames the key. `declaring` adds the object properties of a type whose
    // binder uses a private Raw* record for its scalars (SessionConfig.Tuning).
    private static IEnumerable<PropertyInfo> BoundProperties(Type type, object? instance, Type? declaring = null)
        => type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Concat(declaring is not null && declaring != type
                ? declaring.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => !IsLeaf(Unwrap(p.PropertyType)))
                : [])
            .Where(p => p.GetIndexParameters().Length == 0
                && p.GetMethod is { IsPublic: true }
                && (p.SetMethod is { IsPublic: true } or { IsAssembly: true }
                    || (!IsLeaf(Unwrap(p.PropertyType)) && Read(instance, p) is not null)))
            .OrderBy(KeyName, StringComparer.Ordinal);

    private static string KeyName(PropertyInfo p) => p.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name ?? p.Name;

    // A hand binder's effective object names a timeout `IdleTimeout` where the key is `...Seconds`.
    private static object? Read(object? instance, PropertyInfo p)
    {
        if (instance is null)
            return null;
        if (instance.GetType().GetProperty(p.Name) is { } same)
            return same.GetValue(instance);
        return p.Name.EndsWith("Seconds", StringComparison.Ordinal) && instance.GetType().GetProperty(p.Name[..^"Seconds".Length]) is { } timeout && timeout.GetValue(instance) is TimeSpan span
            ? (int)span.TotalSeconds
            : null;
    }

    // Registers every key of `type` and returns a sample value for each, all different from its default.
    private static JsonNode? Describe(Dictionary<string, Key> shape, string path, Type declared, object? value, int depth, Type? declaring = null)
    {
        if (depth > 12)
            throw new InvalidOperationException($"{path} nests too deep. Does an options class reference itself?");

        var type = Unwrap(declared);
        if (IsLeaf(type))
        {
            var current = value is null ? null : ToJson(value, path);
            shape[path] = new Key(type, current);
            // A credential the schema leaves out stays out of the sample; the rejection test covers it.
            return type == typeof(SensitiveString) && !SchemaNodes.ContainsKey(path) ? null : NonDefault(type, current, path);
        }

        if (Element(type, typeof(IDictionary<,>), 1) is { } valueType)
        {
            shape[path] = new Key(null, null);
            return new JsonObject { ["sample"] = Describe(shape, $"{path}.*", valueType, Create(valueType), depth + 1) };
        }

        if (Element(type, typeof(IEnumerable<>), 0) is { } elementType)
        {
            var item = Describe(shape, $"{path}[]", elementType, Create(elementType), depth + 1);
            shape[path] = new Key(null, null);
            return new JsonArray(item);
        }

        shape[path] = new Key(null, null);
        var sample = new JsonObject();
        foreach (var property in BoundProperties(type, value, declaring))
        {
            var child = Describe(shape, $"{path}.{KeyName(property)}", property.PropertyType, Read(value, property), depth + 1);
            if (child is not null)
                sample[KeyName(property)] = child;
        }

        return sample;
    }

    // SessionConfig also reads each Tuning scalar directly under Session. Probe the binder for them.
    private static void AddRootAliases(Dictionary<string, Key> shape, Bound bound, JsonNode? sample)
    {
        var tuning = shape.Where(p => p.Key.StartsWith(bound.Section + ".Tuning.", StringComparison.Ordinal) && p.Value.Leaf is not null
            && p.Key.Count(c => c == '.') == 2).ToList();
        foreach (var (path, key) in tuning)
        {
            var name = path.Split('.')[^1];
            var probe = NonDefault(key.Leaf!, key.Default, path);
            var json = new JsonObject { [bound.Section] = new JsonObject { [name] = probe!.DeepClone() } };
            if (JsonSerializer.Serialize(Bind(bound.Type, json, bound.Section)) != JsonSerializer.Serialize(Bind(bound.Type, new JsonObject(), bound.Section)))
            {
                shape[$"{bound.Section}.{name}"] = key;
                ((JsonObject)sample!)[name] = probe.DeepClone();
            }
        }
    }

    private static object? Bind(Type type, JsonObject root, string section)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return HandBinder(type)!.Invoke(null, [configuration.GetSection(section)]);
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsLeaf(Type type)
        => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(TimeSpan)
            || type == typeof(DateTimeOffset) || type == typeof(SensitiveString) || type == typeof(JsonObject);

    private static Type? Element(Type type, Type generic, int index)
        => type.IsArray && index == 0
            ? type.GetElementType()
            : type.GetInterfaces().Append(type)
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == generic
                    && (generic != typeof(IDictionary<,>) || i.GetGenericArguments()[0] == typeof(string)))
                .Select(i => i.GetGenericArguments()[index])
                .FirstOrDefault();

    private static object? Create(Type type)
    {
        var t = Unwrap(type);
        return t.IsValueType || t == typeof(string) || t.IsAbstract || t.IsInterface || t.IsArray || t.GetConstructor(Type.EmptyTypes) is null
            ? null
            : Activator.CreateInstance(t);
    }

    // The JSON a default or probe takes. The binder reads enum names without case, but the schema lists
    // one spelling per value (lowercase search backends, kebab-case exposure modes), so look that up.
    private static JsonNode ToJson(object value, string path) => value switch
    {
        bool or int or long or double or string => JsonValue.Create(value)!,
        TimeSpan t => JsonValue.Create(t.ToString("c")),
        DateTimeOffset o => JsonValue.Create(o.ToString("o")),
        Enum e => JsonValue.Create(Spell(path, e.ToString())),
        _ => new JsonObject()
    };

    private static JsonNode? NonDefault(Type leaf, JsonNode? current, string path)
    {
        if (leaf == typeof(bool))
            return JsonValue.Create(current?.GetValue<bool>() is not true);
        if (leaf == typeof(double))
            return JsonValue.Create(Convert.ToDouble(current?.GetValue<object>() ?? 0, System.Globalization.CultureInfo.InvariantCulture) + 0.125);
        if (leaf.IsPrimitive)
            return JsonValue.Create(Convert.ToInt64(current?.GetValue<object>() ?? 0, System.Globalization.CultureInfo.InvariantCulture) + 1);
        if (leaf == typeof(TimeSpan))
            return JsonValue.Create("00:00:07");
        if (leaf == typeof(DateTimeOffset))
            return JsonValue.Create("2030-01-01T00:00:00Z");
        if (leaf == typeof(JsonObject))
            return new JsonObject { ["Option"] = "value" };
        // A string the schema constrains takes a value that satisfies it.
        var nodes = SchemaNodes.GetValueOrDefault(path) ?? [];
        var patterns = nodes.Select(n => n["pattern"]?.GetValue<string>()).OfType<string>().ToList();
        var other = (string v) => Normalize(v) != Normalize(current?.GetValue<string>() ?? string.Empty) && patterns.All(p => Regex.IsMatch(v, p));
        if (leaf.IsEnum)
            return JsonValue.Create(Spell(path, Enum.GetNames(leaf).Where(other).DefaultIfEmpty(Enum.GetNames(leaf)[0]).First()));

        var enums = nodes.SelectMany(n => n["enum"] as JsonArray ?? []).Select(v => v?.GetValue<string>()).OfType<string>();
        return JsonValue.Create(enums.Concat(["https://probe.example/", "probe", "10.0.0.1", "Text"]).FirstOrDefault(other));
    }

    private static string Normalize(string name) => name.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string Spell(string path, string name)
        => SchemaNodes.TryGetValue(path, out var nodes)
            ? nodes.Select(n => n["enum"] as JsonArray).OfType<JsonArray>().SelectMany(a => a).Select(v => v?.GetValue<string>()).OfType<string>()
                .FirstOrDefault(listed => Normalize(listed) == Normalize(name)) ?? name
            : name;

    // ---- the schema -------------------------------------------------------------------

    private static readonly Lazy<Dictionary<string, List<JsonObject>>> SchemaLazy = new(() =>
    {
        var root = (JsonObject)JsonNode.Parse(EmbeddedSchemaLoader.LoadConfigSchema(EmbeddedSchemaLoader.CurrentSchemaVersion)!)!;
        var nodes = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var (name, node) in (JsonObject)root["properties"]!)
            Walk(nodes, root, node, name);
        return nodes;
    });

    private static Dictionary<string, List<JsonObject>> SchemaNodes => SchemaLazy.Value;

    // Paths use the shape's spelling: Name.Child, .* for dictionary values, [] for list items.
    // oneOf and anyOf branches merge into one path. A boolean `false` schema is a deliberate rejection.
    private static void Walk(Dictionary<string, List<JsonObject>> nodes, JsonObject root, JsonNode? node, string path)
    {
        if (node is not JsonObject schema)
            return;

        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            JsonNode? target = root;
            foreach (var part in reference.TrimStart('#', '/').Split('/'))
                target = (target as JsonObject)?[part];
            schema = (JsonObject)target!;
        }

        if (!nodes.TryGetValue(path, out var list))
            nodes[path] = list = [];
        list.Add(schema);

        foreach (var keyword in new[] { "oneOf", "anyOf" })
        {
            foreach (var branch in schema[keyword] as JsonArray ?? [])
                Walk(nodes, root, branch, path);
        }

        foreach (var (name, child) in schema["properties"] as JsonObject ?? [])
            Walk(nodes, root, child, $"{path}.{name}");
        Walk(nodes, root, schema["additionalProperties"] as JsonObject, $"{path}.*");
        Walk(nodes, root, schema["items"] as JsonObject, $"{path}[]");
    }

    private static bool Claimed(string path) => Sections.Any(b => path == b.Section || path.StartsWith(b.Section + ".", StringComparison.Ordinal));

    // ---- tests ------------------------------------------------------------------------

    [Fact]
    public void Every_section_is_declared_by_an_options_class_and_the_schema_lists_it()
    {
        Assert.True(Sections.Count >= 10, "Found almost no options classes. Check that the Netclaw assemblies are in the test output folder.");
        var missing = Sections.Select(b => b.Section).Distinct().Where(s => !SchemaNodes.ContainsKey(s)).ToList();
        Assert.True(missing.Count == 0, $"{string.Join(", ", missing)}: an options class binds this section but the schema has no top-level \"{(missing.Count > 0 ? missing[0] : "")}\" entry. Add it to netclaw-config.v1.schema.json.");
    }

    [Fact]
    public void Schema_and_options_classes_declare_the_same_keys()
    {
        // A credential (SensitiveString) belongs in secrets.json. The schema may leave it out so that
        // doctor rejects it in netclaw.json, and may list it where the config also takes it inline.
        var inCodeNotSchema = Shape.Where(p => !SchemaNodes.ContainsKey(p.Key) && p.Value.Leaf != typeof(SensitiveString))
            .Select(p => $"{p.Key}  ({p.Value.Leaf?.Name ?? "object/list"}{(p.Value.Default is null ? "" : ", default " + p.Value.Default.ToJsonString())})");
        var inSchemaNotCode = SchemaNodes.Keys.Where(p => Claimed(p) && !Shape.ContainsKey(p));

        var problems = inCodeNotSchema.Select(k => $"add to the schema: {k}")
            .Concat(inSchemaNotCode.Select(k => $"remove from the schema, no options class binds it: {k}"))
            .Order(StringComparer.Ordinal).ToList();
        Assert.True(problems.Count == 0, "netclaw-config.v1.schema.json disagrees with the options classes:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void Schema_types_enums_and_defaults_match_the_options_classes()
    {
        var problems = new List<string>();
        foreach (var (path, key) in Shape.Where(p => SchemaNodes.ContainsKey(p.Key) && p.Value.Leaf is not null).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var nodes = SchemaNodes[path];
            var expected = key.Leaf == typeof(bool) ? "boolean" : key.Leaf == typeof(JsonObject) ? "object"
                : key.Leaf == typeof(double) || key.Leaf == typeof(float) || key.Leaf == typeof(decimal) ? "number"
                : key.Leaf!.IsPrimitive ? "integer" : "string";
            var declared = nodes.Select(n => n["type"]).OfType<JsonNode>().SelectMany(t => t is JsonArray a ? a.Select(x => x!.GetValue<string>()) : [t.GetValue<string>()]).ToList();
            if (declared.Count > 0 && !declared.Any(t => t == expected || (t == "number" && expected == "integer")))
                problems.Add($"{path}: {key.Leaf!.Name} binds as JSON {expected}; change the schema type from {string.Join("/", declared)}");

            foreach (var listed in nodes.Select(n => n["enum"] as JsonArray).OfType<JsonArray>().Where(_ => key.Leaf!.IsEnum))
            {
                var values = listed.Select(v => v?.GetValue<string>()).OfType<string>().Select(Normalize).ToHashSet();
                var members = Enum.GetNames(key.Leaf!).Select(Normalize).ToHashSet();
                if (members.Except(values).ToList() is { Count: > 0 } missing)
                    problems.Add($"{path}: add {string.Join(", ", missing)} to the schema enum ({key.Leaf!.Name} has the member)");
                if (values.Except(members).ToList() is { Count: > 0 } extra)
                    problems.Add($"{path}: remove {string.Join(", ", extra)} from the schema enum ({key.Leaf!.Name} lacks it)");
            }

            if (key.Default is not null)
            {
                foreach (var node in nodes.Where(n => n.ContainsKey("default") && !JsonNode.DeepEquals(n["default"], key.Default)))
                    problems.Add($"{path}: the code default is {key.Default.ToJsonString()}; change the schema default from {node["default"]?.ToJsonString() ?? "null"}");
            }
        }

        Assert.True(problems.Count == 0, "netclaw-config.v1.schema.json disagrees with the options classes:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public async Task Doctor_accepts_and_fix_keeps_every_key_the_options_classes_define()
    {
        var problems = new List<string>();
        foreach (var (name, sample) in Samples)
        {
            var section = name.Split('|')[0];
            var config = new JsonObject { ["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion, [section] = sample.DeepClone() };
            var result = await RunDoctorAsync(config);
            if (result.Severity != DoctorSeverity.Pass)
                problems.Add($"{name}: doctor rejects what the options class binds: {result.Message}");

            // The fix resolver deletes keys the schema disallows and keeps no backup.
            var schemaText = EmbeddedSchemaLoader.LoadConfigSchema(EmbeddedSchemaLoader.CurrentSchemaVersion)!;
            var fixing = (JsonObject)config.DeepClone();
            SchemaFixResolver.TryApplySchemaFixes(JsonSchema.FromText(schemaText), (JsonObject)JsonNode.Parse(schemaText)!, fixing, out var fixes);
            if (!JsonNode.DeepEquals(config, fixing))
                problems.Add($"{name}: doctor --fix would change the config ({string.Join("; ", fixes)})");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task Credentials_missing_from_the_schema_are_rejected_in_netclaw_json()
    {
        var rejected = Shape.Where(p => p.Value.Leaf == typeof(SensitiveString) && !SchemaNodes.ContainsKey(p.Key)).Select(p => p.Key).ToList();
        Assert.NotEmpty(rejected);

        foreach (var path in rejected)
        {
            var segments = path.Split('.').Select(s => s == "*" ? "sample" : s).ToArray();
            JsonNode value = JsonValue.Create("secret")!;
            foreach (var segment in segments.Reverse())
                value = new JsonObject { [segment] = value };
            var config = (JsonObject)value;
            config["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;

            var result = await RunDoctorAsync(config);
            Assert.True(result.Severity == DoctorSeverity.Error && result.Message.Contains(segments[^1], StringComparison.Ordinal),
                $"{path} is a credential that the schema leaves out, so doctor must reject it in netclaw.json: {result.Message}");
        }
    }

    [Fact]
    public void Hand_written_binders_read_every_key_of_their_options_class()
    {
        var unread = new List<string>();
        foreach (var bound in Sections.Where(b => HandBinder(b.Type) is not null))
        {
            var baseline = JsonSerializer.Serialize(Bind(bound.Type, new JsonObject(), bound.Section));
            var sample = (JsonObject)Samples[$"{bound.Section}|{bound.Type.FullName}"];
            foreach (var (key, value) in sample)
            {
                var config = new JsonObject { [bound.Section] = new JsonObject { [key] = value!.DeepClone() } };
                if (JsonSerializer.Serialize(Bind(bound.Type, config, bound.Section)) == baseline)
                    unread.Add($"{bound.Section}.{key}");
            }
        }

        Assert.True(unread.Count == 0, $"BindFromConfiguration does not read: {string.Join(", ", unread)}. Read the key in the binder, or remove the property.");
    }

    [Fact]
    public void Binding_sites_use_the_section_constants_not_string_literals()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Netclaw.Configuration")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Cannot find the repository root.");

        var names = string.Join('|', Sections.Select(b => b.Section).Distinct());
        var literal = new Regex($"""GetSection\(\s*"({names})"\s*\)""");
        var hits = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("Tests", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i)).Where(t => literal.IsMatch(t.line)))
            .Select(t => $"{Path.GetRelativePath(root, t.f)}:{t.i + 1}  {t.line.Trim()}")
            .ToList();
        Assert.True(hits.Count == 0, "Bind through the options class constant (for example MemoryConfig.SectionName) so this test and the binder cannot drift:\n  " + string.Join("\n  ", hits));

        // The channel builder binds the section named after the ChannelType member.
        var channels = Sections.Where(b => typeof(IRemoteChatChannelOptions).IsAssignableFrom(b.Type)).ToList();
        Assert.All(channels, b => Assert.Contains(b.Section, Enum.GetNames<ChannelType>()));
    }

    private async Task<DoctorCheckResult> RunDoctorAsync(JsonObject config)
    {
        var paths = new NetclawPaths(_temp.Path);
        paths.EnsureDirectoriesExist();
        await File.WriteAllTextAsync(paths.NetclawConfigPath, config.ToJsonString(), TestContext.Current.CancellationToken);
        return await new ConfigSchemaDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);
    }
}
