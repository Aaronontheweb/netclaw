// -----------------------------------------------------------------------
// <copyright file="WizardSecretsBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

/// <summary>
/// Tests for the semantic merge behavior on secrets.json. Covers blank-keep,
/// replace-on-non-blank, and explicit-remove paths called out by the
/// section-editor-abstraction spec.
/// </summary>
public sealed class WizardSecretsBuilderTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public WizardSecretsBuilderTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Write_ReplacesOnNonBlank_PreservesBlankOmittedSiblings()
    {
        // Pre-existing: Slack section with both tokens stored.
        File.WriteAllText(_paths.SecretsPath, """
            {"Slack": {"BotToken": "ENC:old-bot", "AppToken": "ENC:old-app"}}
            """);

        var builder = new WizardSecretsBuilder(_paths);
        // Editor produces only the BotToken (operator left AppToken blank).
        builder.AddSection("Slack", new Dictionary<string, object>
        {
            ["BotToken"] = "new-bot",
        });
        builder.WriteSecretsFile();

        var written = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!.AsObject();
        var slack = written["Slack"]!.AsObject();

        // Blank-omitted AppToken SHALL be preserved (encrypted bytes stay verbatim
        // because SecretsFileWriter is idempotent on ENC:-prefixed values).
        Assert.Equal("ENC:old-app", slack["AppToken"]!.GetValue<string>());
        // BotToken SHALL be replaced with the new value.
        var botToken = slack["BotToken"]!.GetValue<string>();
        Assert.NotEqual("ENC:old-bot", botToken);
        Assert.Contains("new-bot", botToken);
    }

    [Fact]
    public void Write_AddsNewSection_WithoutTouchingUnrelatedSections()
    {
        File.WriteAllText(_paths.SecretsPath, """
            {"Discord": {"BotToken": "ENC:keep-me"}}
            """);

        var builder = new WizardSecretsBuilder(_paths);
        builder.AddSection("Slack", new Dictionary<string, object>
        {
            ["BotToken"] = "added",
        });
        builder.WriteSecretsFile();

        var obj = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!.AsObject();
        Assert.Equal("ENC:keep-me", obj["Discord"]!["BotToken"]!.GetValue<string>());
        Assert.True(obj.ContainsKey("Slack"));
    }

    [Fact]
    public void Write_RemoveValue_DeletesTopLevelKey()
    {
        File.WriteAllText(_paths.SecretsPath, """
            {"DeviceToken": "ENC:to-be-removed", "Slack": {"BotToken": "ENC:keep"}}
            """);

        var builder = new WizardSecretsBuilder(_paths);
        builder.RemoveValue("DeviceToken");
        // Force a write even though no additions exist.
        builder.AddSection("Slack", new Dictionary<string, object>()); // no-op
        builder.WriteSecretsFile();

        var obj = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!.AsObject();
        Assert.False(obj.ContainsKey("DeviceToken"));
        Assert.Equal("ENC:keep", obj["Slack"]!["BotToken"]!.GetValue<string>());
    }

    [Fact]
    public void Write_RemoveSectionKey_DeletesNestedField_AndPrunesEmptySection()
    {
        File.WriteAllText(_paths.SecretsPath, """
            {"Slack": {"BotToken": "ENC:gone", "AppToken": "ENC:stay"}}
            """);

        var builder = new WizardSecretsBuilder(_paths);
        builder.RemoveSectionKey("Slack", "BotToken");
        builder.WriteSecretsFile();

        var obj = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!.AsObject();
        var slack = obj["Slack"]!.AsObject();
        Assert.False(slack.ContainsKey("BotToken"));
        Assert.Equal("ENC:stay", slack["AppToken"]!.GetValue<string>());
    }

    [Fact]
    public void Write_RemoveSectionKey_EmptiesSection_DropsTheSection()
    {
        File.WriteAllText(_paths.SecretsPath, """
            {"Slack": {"BotToken": "ENC:gone"}, "Discord": {"BotToken": "ENC:keep"}}
            """);

        var builder = new WizardSecretsBuilder(_paths);
        builder.RemoveSectionKey("Slack", "BotToken");
        builder.WriteSecretsFile();

        var obj = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!.AsObject();
        Assert.False(obj.ContainsKey("Slack"));
        Assert.True(obj.ContainsKey("Discord"));
    }

    [Fact]
    public void Write_NoSecretsNoRemovals_DoesNothing()
    {
        var builder = new WizardSecretsBuilder(_paths);
        builder.WriteSecretsFile();

        Assert.False(File.Exists(_paths.SecretsPath));
    }

    [Fact]
    public void RemoveValue_BlankKey_Throws()
    {
        var builder = new WizardSecretsBuilder(_paths);
        Assert.Throws<ArgumentException>(() => builder.RemoveValue(""));
        Assert.Throws<ArgumentException>(() => builder.RemoveValue("  "));
    }

    [Fact]
    public void Write_RemoveThenAdd_SameKey_NewValueWins()
    {
        File.WriteAllText(_paths.SecretsPath, """{"DeviceToken": "ENC:old"}""");

        var builder = new WizardSecretsBuilder(_paths);
        builder.RemoveValue("DeviceToken");
        builder.AddValue("DeviceToken", "new-value");
        builder.WriteSecretsFile();

        var obj = JsonNode.Parse(File.ReadAllText(_paths.SecretsPath))!.AsObject();
        Assert.Contains("new-value", obj["DeviceToken"]!.GetValue<string>());
    }
}
