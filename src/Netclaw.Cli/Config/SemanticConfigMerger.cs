// -----------------------------------------------------------------------
// <copyright file="SemanticConfigMerger.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Json;

namespace Netclaw.Cli.Config;

/// <summary>
/// Semantic merge layer for JSON-shaped configuration. Deep-merges a
/// "new partial" (what a wizard run would write) onto the existing
/// on-disk state, preserving unrelated sections and inactive nested
/// values. Both inputs are normalized through <see cref="JsonNode"/> so
/// callers can mix <see cref="Dictionary{TKey,TValue}"/>,
/// <see cref="JsonElement"/>, primitive arrays, and string-typed nested
/// maps without type-cast gymnastics.
/// </summary>
/// <remarks>
/// <para><b>Merge rules:</b></para>
/// <list type="bullet">
///   <item>For each key in <c>newPartial</c>: if BOTH sides resolve to a
///     JSON object, the objects are merged recursively. Otherwise the
///     new value wins (scalars, arrays, type mismatches).</item>
///   <item>For each key only in <c>existing</c>: preserved as-is.</item>
///   <item>Arrays are NOT element-merged — the new array replaces the old.
///     Editors that need to add elements SHALL emit the full new collection.</item>
/// </list>
/// <para>
/// Byte-identical serialization is NOT part of the contract: property
/// order and formatting MAY change across a merge. What's preserved is
/// the <b>meaning</b> of unrelated values.
/// </para>
/// </remarks>
public static class SemanticConfigMerger
{
    /// <summary>
    /// Deep-merge <paramref name="newPartial"/> onto <paramref name="existing"/>
    /// and return a fresh dictionary representing the result. Neither input
    /// is mutated.
    /// </summary>
    public static Dictionary<string, object> Merge(
        IReadOnlyDictionary<string, object> existing,
        IReadOnlyDictionary<string, object> newPartial)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(newPartial);

        var existingNode = ToJsonObject(existing);
        var newNode = ToJsonObject(newPartial);

        var merged = MergeObjects(existingNode, newNode);
        return JsonObjectToDict(merged);
    }

    /// <summary>
    /// Deep-merge two <see cref="JsonObject"/> trees per the same rules.
    /// Returns a fresh tree; inputs are not mutated.
    /// </summary>
    public static JsonObject MergeObjects(JsonObject existing, JsonObject newPartial)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(newPartial);

        var result = (JsonObject)existing.DeepClone();

        foreach (var (key, newNode) in newPartial)
        {
            if (newNode is null)
            {
                result[key] = null;
                continue;
            }

            if (result.TryGetPropertyValue(key, out var existingNode)
                && existingNode is JsonObject existingObj
                && newNode is JsonObject newObj)
            {
                result[key] = MergeObjects(existingObj, newObj);
            }
            else
            {
                result[key] = newNode.DeepClone();
            }
        }

        return result;
    }

    /// <summary>
    /// Convert a dictionary (with possibly mixed nested types — including
    /// <see cref="JsonElement"/>, <see cref="Dictionary{TKey,TValue}"/>, primitive
    /// arrays, and value-typed string maps) into a normalized <see cref="JsonObject"/>.
    /// </summary>
    public static JsonObject ToJsonObject(IReadOnlyDictionary<string, object> dict)
    {
        ArgumentNullException.ThrowIfNull(dict);
        var json = JsonSerializer.Serialize(dict, JsonDefaults.ConfigFile);
        return JsonNode.Parse(json)!.AsObject();
    }

    /// <summary>
    /// Convert a normalized <see cref="JsonObject"/> back into a
    /// <see cref="Dictionary{TKey,TValue}"/> for downstream serializers
    /// that expect that shape.
    /// </summary>
    public static Dictionary<string, object> JsonObjectToDict(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var json = obj.ToJsonString(JsonDefaults.ConfigFile);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
    }
}
