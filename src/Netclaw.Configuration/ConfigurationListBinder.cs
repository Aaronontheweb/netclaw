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
/// security policy. This binder runs the normal binder first, then replaces each list or
/// array that the section configures with a list bound only from the section.
/// </para>
/// <para>Rules for each list or array property:</para>
/// <list type="bullet">
/// <item>Absent key: the default list stays.</item>
/// <item>Key with child items: the configured items replace the default list.</item>
/// <item>Key with an empty value (JSON <c>[]</c> or an empty environment variable): the list becomes empty.</item>
/// <item>Key with a non-empty scalar value: binding fails, because a list cannot hold a scalar.</item>
/// <item>JSON <c>null</c>: <see cref="IConfiguration"/> treats it as absent, so the default list stays.</item>
/// </list>
/// <para>
/// Dictionaries keep the binder's merge-by-key behavior. A configured key already replaces
/// the value for that key, and a new dictionary would lose the key comparer of the default.
/// </para>
/// </remarks>
public static class ConfigurationListBinder
{
    public static T Get<T>(IConfigurationSection section) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(section);

        var instance = section.Get<T>() ?? new T();
        ReplaceConfiguredLists(instance, section);
        return instance;
    }

    private static void ReplaceConfiguredLists(object target, IConfigurationSection section)
    {
        foreach (var child in section.GetChildren())
        {
            var property = FindBindableProperty(target.GetType(), child.Key);
            if (property is null)
                continue;

            var propertyType = property.PropertyType;
            if (IsList(propertyType))
            {
                var replacement = BindList(child, propertyType);
                if (replacement is not null)
                    property.SetValue(target, replacement);
            }
            else if (IsNestedObject(propertyType) && property.GetValue(target) is { } nested)
            {
                ReplaceConfiguredLists(nested, child);
            }
        }
    }

    private static object? BindList(IConfigurationSection section, Type listType)
    {
        if (section.GetChildren().Any())
        {
            return section.Get(listType)
                ?? throw new InvalidOperationException(
                    $"Configuration key '{section.Path}' could not be bound as a list.");
        }

        return section.Value switch
        {
            null => null,
            "" => CreateEmptyList(listType),
            // The value is not echoed: a misplaced secret must not reach a startup error.
            _ => throw new InvalidOperationException(
                $"Configuration key '{section.Path}' must be a list, but it has a scalar value.")
        };
    }

    private static object CreateEmptyList(Type listType)
    {
        if (listType.IsArray)
            return Array.CreateInstance(listType.GetElementType()!, 0);

        if (listType.IsInterface && listType.IsGenericType)
        {
            var listOfElement = typeof(List<>).MakeGenericType(listType.GetGenericArguments()[0]);
            if (listType.IsAssignableFrom(listOfElement))
                return Activator.CreateInstance(listOfElement)!;
        }

        return Activator.CreateInstance(listType)
            ?? throw new InvalidOperationException($"Cannot create an empty {listType.Name}.");
    }

    // Matches the Microsoft binder: public instance properties with a public setter
    // (init setters included), and a case-insensitive key match.
    private static PropertyInfo? FindBindableProperty(Type type, string key)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetIndexParameters().Length == 0
                && p.SetMethod?.IsPublic == true
                && string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));

    private static bool IsList(Type type)
        => type != typeof(string)
            && typeof(IEnumerable).IsAssignableFrom(type)
            && !IsDictionary(type);

    private static bool IsDictionary(Type type)
        => typeof(IDictionary).IsAssignableFrom(type)
            || type.GetInterfaces().Append(type).Any(i => i.IsGenericType
                && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    // The binder treats a type with a string converter as a scalar value, not as an object.
    private static bool IsNestedObject(Type type)
        => type.IsClass
            && type != typeof(string)
            && !typeof(IEnumerable).IsAssignableFrom(type)
            && !TypeDescriptor.GetConverter(type).CanConvertFrom(typeof(string));
}
