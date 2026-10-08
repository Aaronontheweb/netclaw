// -----------------------------------------------------------------------
// <copyright file="DoctorFixKeepsUserConfigTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

/// <summary>
/// <c>netclaw doctor --fix</c> must not delete a value the operator wrote without a backup of the
/// whole file, and must never delete Models settings or credentials.
/// </summary>
[Collection(Netclaw.Cli.Tests.LegacyModelEnvironmentCollection.Name)]
public sealed class DoctorFixKeepsUserConfigTests : IDisposable
{
    private const string Secret = "xoxb-test-secret-value";
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> InvalidModelsShapes() => new()
    {
        """{"Main":{"Provider":"p","ModelId":"a"},"Definitions":{"d":{"Provider":"p","ModelId":"a"}},"Roles":{"Main":"d"}}""",
        """{"Definitions":{"d":{"Provider":"p","ModelId":"a"}}}""",
        """{"Roles":{"Main":"d"}}""",
        """{"Definitions":{"d":{"Provider":"p","ModelId":"a"}},"Roles":{}}""",
    };

    [Theory]
    [MemberData(nameof(InvalidModelsShapes))]
    public async Task InvalidModelsSectionIsLeftAlone(string models)
    {
        var paths = NewPaths();
        var original = """{"configVersion":1,"Providers":{"p":{"Type":"ollama"}},"Models":""" + models + "}";
        File.WriteAllText(paths.NetclawConfigPath, original);

        var plan = await ApplyAsync(paths);

        Assert.False(plan.HasChanges);
        Assert.Equal(original, File.ReadAllText(paths.NetclawConfigPath));
        Assert.Empty(Backups(paths));
    }

    [Fact]
    public async Task CredentialInConfigFileIsKeptWhileAnUnknownKeyIsRemovedWithBackup()
    {
        var paths = NewPaths();
        var original = """{"configVersion":1,"Slack":{"Enabled":false,"BotToken":"xoxb-test-secret-value","Comment":"mine"}}""";
        File.WriteAllText(paths.NetclawConfigPath, original);

        var service = Service(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        var backups = await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        var fix = Assert.Single(plan.Fixes);
        Assert.Contains("Removed disallowed property /Slack/Comment", fix.Description);
        Assert.DoesNotContain(Secret, fix.Description);
        Assert.DoesNotContain("BotToken", fix.Description);

        var backup = Assert.Single(backups);
        Assert.EndsWith(".removed-keys.bak", backup);
        Assert.Equal(original, File.ReadAllText(backup));

        var updated = File.ReadAllText(paths.NetclawConfigPath);
        Assert.Contains(Secret, updated);
        Assert.DoesNotContain("Comment", updated);
    }

    [Fact]
    public async Task CredentialAloneIsNotRemoved()
    {
        var paths = NewPaths();
        var original = """{"configVersion":1,"Slack":{"Enabled":false,"BotToken":"xoxb-test-secret-value"}}""";
        File.WriteAllText(paths.NetclawConfigPath, original);

        var plan = await ApplyAsync(paths);

        Assert.False(plan.HasChanges);
        Assert.Equal(original, File.ReadAllText(paths.NetclawConfigPath));
        Assert.Empty(Backups(paths));
    }

    [Fact]
    public async Task UnknownKeysThatTheSchemaAllowsAreNotTouched()
    {
        var paths = NewPaths();
        var original = """{"configVersion":1,"Comment":"top","Providers":{"p":{"Type":"ollama","Comment":"entry"}}}""";
        File.WriteAllText(paths.NetclawConfigPath, original);

        var plan = await ApplyAsync(paths);

        Assert.False(plan.HasChanges);
        Assert.Equal(original, File.ReadAllText(paths.NetclawConfigPath));
        Assert.Empty(Backups(paths));
    }

    [Fact]
    public async Task LegacyModelsAreStillMigratedWithBackup()
    {
        var paths = NewPaths();
        var original = """{"configVersion":1,"Models":{"Main":{"Provider":"p","ModelId":"a"}}}""";
        File.WriteAllText(paths.NetclawConfigPath, original);

        var service = Service(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        var backups = await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        var backup = Assert.Single(backups);
        Assert.EndsWith(".legacy-models.bak", backup);
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.Contains("Definitions", File.ReadAllText(paths.NetclawConfigPath));
    }

    [Fact]
    public async Task EachRunWritesItsOwnBackup()
    {
        var paths = NewPaths();
        var first = """{"configVersion":1,"Slack":{"Enabled":false,"Comment":"one"}}""";
        File.WriteAllText(paths.NetclawConfigPath, first);
        await ApplyAsync(paths);

        var second = """{"configVersion":1,"Slack":{"Enabled":false,"Comment":"two"}}""";
        File.WriteAllText(paths.NetclawConfigPath, second);
        await ApplyAsync(paths);

        Assert.Equal(first, File.ReadAllText(paths.NetclawConfigPath + ".removed-keys.bak"));
        Assert.Equal(second, File.ReadAllText(paths.NetclawConfigPath + ".removed-keys.2.bak"));
    }

    [Fact]
    public async Task FailedBackupLeavesTheFileUntouched()
    {
        var paths = NewPaths();
        var original = """{"configVersion":1,"Slack":{"Enabled":false,"Comment":"mine"}}""";
        File.WriteAllText(paths.NetclawConfigPath, original);
        // A directory at the backup name makes the copy fail on every platform and for root.
        Directory.CreateDirectory(paths.NetclawConfigPath + ".removed-keys.bak");

        var service = Service(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        Assert.True(plan.HasChanges);

        await Assert.ThrowsAnyAsync<IOException>(() => service.ApplyAsync(plan, TestContext.Current.CancellationToken));
        Assert.Equal(original, File.ReadAllText(paths.NetclawConfigPath));
    }

    private static async Task<DoctorFixPlan> ApplyAsync(NetclawPaths paths)
    {
        var service = Service(paths);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        await service.ApplyAsync(plan, TestContext.Current.CancellationToken);
        return plan;
    }

    private static string[] Backups(NetclawPaths paths)
        => Directory.GetFiles(Path.GetDirectoryName(paths.NetclawConfigPath)!, "*.bak");

    private static DoctorFixService Service(NetclawPaths paths)
        => new(paths, Path.Combine(paths.BasePath, "unused.service"), systemdEnabled: false);

    private NetclawPaths NewPaths()
    {
        var path = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var paths = new NetclawPaths(path);
        paths.EnsureDirectoriesExist();
        return paths;
    }
}
