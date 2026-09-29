// -----------------------------------------------------------------------
// <copyright file="ConfigurationListBinderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Pins the list shapes that <see cref="ConfigurationListBinder"/> handles beyond the
/// <see cref="ToolConfig"/> graph. A shape that the binder cannot replace must fail binding,
/// because the Microsoft binder would silently add configured items to the defaults.
/// </summary>
public sealed class ConfigurationListBinderTests
{
    [Fact]
    public void Lists_inside_list_items_replace_the_item_defaults()
    {
        var bound = Bind<ItemsHolder>(new()
        {
            ["Items:0:Tags:0"] = "configured"
        });

        var item = Assert.Single(bound.Items);
        Assert.Equal(["configured"], item.Tags);
    }

    [Fact]
    public void Lists_inside_dictionary_values_replace_the_default_values()
    {
        var bound = Bind<GrantsHolder>(new()
        {
            ["Grants:server:0"] = "configured"
        });

        Assert.Equal(["configured"], bound.Grants["server"]);
        Assert.Equal(["default"], bound.Grants["other"]);
    }

    [Fact]
    public void Configured_list_without_a_public_setter_fails_loudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Bind<GetOnlyHolder>(new()
        {
            ["Values:0"] = "configured"
        }));

        Assert.Contains("Root.Values", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no public setter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unconfigured_list_without_a_public_setter_keeps_its_defaults()
    {
        var bound = Bind<GetOnlyHolder>(new() { ["Other"] = "value" });

        Assert.Equal(["default"], bound.Values);
    }

    private static T Bind<T>(Dictionary<string, string?> values) where T : class, new()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => $"Root:{pair.Key}", pair => pair.Value))
            .Build();

        return ConfigurationListBinder.Get<T>(configuration.GetSection("Root"), out _);
    }

    private sealed class ItemsHolder
    {
        public List<TaggedItem> Items { get; set; } = [];
    }

    private sealed class TaggedItem
    {
        public List<string> Tags { get; set; } = ["default"];
    }

    private sealed class GrantsHolder
    {
        public Dictionary<string, List<string>> Grants { get; set; } = new(StringComparer.Ordinal)
        {
            ["server"] = ["default"],
            ["other"] = ["default"]
        };
    }

    private sealed class GetOnlyHolder
    {
        public List<string> Values { get; } = ["default"];

        public string? Other { get; set; }
    }
}
