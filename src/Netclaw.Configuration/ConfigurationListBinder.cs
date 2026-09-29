// -----------------------------------------------------------------------
// <copyright file="ConfigurationListBinder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>
/// Binds a configuration section so that a configured list replaces the default list.
/// </summary>
/// <remarks>
/// <para>
/// The Microsoft configuration binder adds configured items to a list that already has
/// items. A config type with default grants, such as the Team tool allowlist, then keeps
/// every default grant when the operator writes a shorter list. That silently widens a
/// security policy. This binder runs the normal binder first. Then it replaces each list
/// or array that the section configures with a list bound only from the section.
/// </para>
/// <para>Rules for each configured list or array:</para>
/// <list type="bullet">
/// <item>Absent key: the default list stays.</item>
/// <item>Key with items: the configured items replace the default list.</item>
/// <item>Empty value (JSON <c>[]</c> or an empty environment variable): the list becomes empty.</item>
/// <item>JSON <c>null</c> or <c>{}</c>: the list becomes empty, and the binder returns a warning.</item>
/// <item>A non-empty scalar value: binding fails.</item>
/// <item>An empty or scalar value from one source and items from another source: binding fails.</item>
/// <item>An item that cannot convert to the element type, or an undefined enum value: binding fails.</item>
/// </list>
/// <para>
/// The binder also replaces lists inside list items and inside dictionary values.
/// Dictionaries with scalar values keep the binder's merge-by-key behavior, because a
/// configured key already replaces the value for that key.
/// </para>
/// <para>
/// Binding fails when the section configures a list that this binder cannot replace: a
/// list property without a public setter, or a list in a dictionary that does not
/// implement <see cref="IDictionary"/> with string keys.
/// </para>
/// </remarks>
public static class ConfigurationListBinder
{
    public static T Get<T>(IConfigurationSection section, out IReadOnlyList<string> warnings)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(section);

        var instance = section.Get<T>() ?? new T();
        var found = new List<string>();
        ReplaceConfiguredLists(instance, section, found);
        warnings = found;
        return instance;
    }

    private static void ReplaceConfiguredLists(object target, IConfigurationSection section, List<string> warnings)
    {
        foreach (var child in section.GetChildren())
        {
            var property = FindProperty(target.GetType(), child.Key);
            if (property is null)
                continue;

            var propertyType = property.PropertyType;
            if (IsList(propertyType))
            {
                if (property.SetMethod?.IsPublic != true)
                {
                    throw new InvalidOperationException(
                        $"Configuration key '{DisplayPath(child)}' is a list that Netclaw cannot replace, "
                        + $"because {target.GetType().Name}.{property.Name} has no public setter.");
                }

                property.SetValue(target, BindList(child, propertyType, warnings));
            }
            else if (IsDictionary(propertyType))
            {
                ReplaceDictionaryLists(property.GetValue(target), propertyType, child, warnings);
            }
            else if (IsNestedObject(propertyType) && property.GetValue(target) is { } nested)
            {
                ReplaceConfiguredLists(nested, child, warnings);
            }
        }
    }

    private static void ReplaceDictionaryLists(
        object? dictionary,
        Type dictionaryType,
        IConfigurationSection section,
        List<string> warnings)
    {
        var (keyType, valueType) = GetDictionaryTypes(dictionaryType);
        var valueIsList = IsList(valueType);
        if (dictionary is null || (!valueIsList && !IsNestedObject(valueType)))
            return;

        if (dictionary is not IDictionary entries || keyType != typeof(string))
        {
            throw new InvalidOperationException(
                $"Configuration key '{DisplayPath(section)}' holds lists that Netclaw cannot replace, "
                + $"because {dictionaryType.Name} is not a dictionary with string keys.");
        }

        foreach (var entry in section.GetChildren())
        {
            if (valueIsList)
                entries[entry.Key] = BindList(entry, valueType, warnings);
            else if (entries[entry.Key] is { } nested)
                ReplaceConfiguredLists(nested, entry, warnings);
        }
    }

    private static object BindList(IConfigurationSection section, Type listType, List<string> warnings)
    {
        var items = section.GetChildren().ToList();
        if (items.Count == 0)
        {
            return section.Value switch
            {
                "" => CreateEmptyList(listType),

                // JSON null and {} both reach here as a key with a null value and no children.
                // An empty list is safe only because every list with default items today is an
                // allow list, so empty grants less. The guard test in ToolConfigBindingTests fails
                // when a config type gains a new list with default items, which forces a new
                // decision for that list.
                null => WarnAndCreateEmptyList(section, listType, warnings),

                // The value is not echoed: a misplaced secret must not reach a startup error.
                _ => throw new InvalidOperationException(
                    $"Configuration key '{DisplayPath(section)}' must be a list, but it has a scalar value.")
            };
        }

        if (section.Value is not null)
        {
            // IConfiguration merges sources. An empty value in one source (for example an
            // empty NETCLAW_* variable) cannot remove items from another source (netclaw.json).
            throw new InvalidOperationException(
                $"Configuration key '{DisplayPath(section)}' has list items and also an empty or scalar value. "
                + "One configuration source sets items and another source sets a value. "
                + "Remove one of them, for example the NETCLAW_* environment variable.");
        }

        var bound = section.Get(listType)
            ?? throw new InvalidOperationException(
                $"Configuration key '{DisplayPath(section)}' could not be bound as a list.");
        var elements = ((IEnumerable)bound).Cast<object?>().ToList();
        var elementType = GetElementType(listType);
        ValidateItems(section, items, elementType, elements.Count);

        if (IsNestedObject(elementType))
        {
            // The binder creates each item from the same ordered children, so index i matches.
            for (var i = 0; i < elements.Count; i++)
            {
                if (elements[i] is { } element)
                    ReplaceConfiguredLists(element, items[i], warnings);
            }
        }

        return bound;
    }

    // The Microsoft binder silently drops an item that it cannot convert. For a grant list,
    // that hides an operator typo, so an unconvertible item fails binding.
    private static void ValidateItems(
        IConfigurationSection section,
        IReadOnlyList<IConfigurationSection> items,
        Type elementType,
        int boundCount)
    {
        var scalarType = Nullable.GetUnderlyingType(elementType) ?? elementType;
        if (scalarType.IsEnum)
        {
            foreach (var item in items)
            {
                if (item.Value is null
                    || !Enum.TryParse(scalarType, item.Value, ignoreCase: true, out var parsed)
                    || !Enum.IsDefined(scalarType, parsed!))
                {
                    throw new InvalidOperationException(
                        $"Configuration key '{DisplayPath(item)}' has the value '{item.Value}', "
                        + $"which is not a valid {scalarType.Name}.");
                }
            }
        }

        if (boundCount != items.Count)
        {
            throw new InvalidOperationException(
                $"Configuration key '{DisplayPath(section)}' has {items.Count} items, "
                + $"but only {boundCount} items are valid {scalarType.Name} values.");
        }
    }

    private static object WarnAndCreateEmptyList(IConfigurationSection section, Type listType, List<string> warnings)
    {
        warnings.Add($"{DisplayPath(section)} is null or an empty object; treating it as an empty list.");
        return CreateEmptyList(listType);
    }

    private static object CreateEmptyList(Type listType)
    {
        var elementType = GetElementType(listType);
        if (listType.IsArray)
            return Array.CreateInstance(elementType, 0);

        if (listType.IsInterface)
        {
            var list = typeof(List<>).MakeGenericType(elementType);
            if (listType.IsAssignableFrom(list))
                return Activator.CreateInstance(list)!;

            var set = typeof(HashSet<>).MakeGenericType(elementType);
            if (listType.IsAssignableFrom(set))
                return Activator.CreateInstance(set)!;
        }

        return Activator.CreateInstance(listType)
            ?? throw new InvalidOperationException($"Netclaw cannot create an empty {listType.Name}.");
    }

    private static string DisplayPath(IConfigurationSection section) => section.Path.Replace(':', '.');

    // Matches the Microsoft binder: public instance properties with a case-insensitive key match.
    private static PropertyInfo? FindProperty(Type type, string key)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetIndexParameters().Length == 0
                && p.GetMethod?.IsPublic == true
                && string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));

    internal static bool IsList(Type type)
        => type != typeof(string)
            && typeof(IEnumerable).IsAssignableFrom(type)
            && !IsDictionary(type);

    internal static bool IsDictionary(Type type)
        => typeof(IDictionary).IsAssignableFrom(type)
            || SelfAndInterfaces(type).Any(i => i.IsGenericType
                && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    // The binder treats a type with a string converter as a scalar value, not as an object.
    internal static bool IsNestedObject(Type type)
        => type.IsClass
            && type != typeof(string)
            && !typeof(IEnumerable).IsAssignableFrom(type)
            && !TypeDescriptor.GetConverter(type).CanConvertFrom(typeof(string));

    private static Type GetElementType(Type listType)
    {
        if (listType.IsArray)
            return listType.GetElementType()!;

        return SelfAndInterfaces(listType)
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .GetGenericArguments()[0];
    }

    private static (Type Key, Type Value) GetDictionaryTypes(Type dictionaryType)
    {
        var generic = SelfAndInterfaces(dictionaryType)
            .First(i => i.IsGenericType
                && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        var arguments = generic.GetGenericArguments();
        return (arguments[0], arguments[1]);
    }

    private static IEnumerable<Type> SelfAndInterfaces(Type type) => type.GetInterfaces().Prepend(type);
}
