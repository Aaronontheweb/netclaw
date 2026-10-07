// -----------------------------------------------------------------------
// <copyright file="McpServerLifecycleTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Mcp;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Cli.Tests.Mcp;

/// <summary>
/// Re-running <c>mcp add</c> must update only the connection definition, and
/// <c>mcp remove</c> must clear everything that is keyed by the server name.
/// </summary>
public sealed class McpServerLifecycleTests : IDisposable
{
    private const string Url = "https://mcp.example.test/mcp";
    private static readonly McpServerName Notion = new("notion");

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly StringWriter _output = new();

    public McpServerLifecycleTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _output.Dispose();
        _dir.Dispose();
    }

    [Theory]
    [InlineData("--header", "X-Api-Key: rotated")]
    [InlineData("--client-secret", "rotated-secret")]
    public async Task Add_ExistingServer_UpdatesDefinitionAndLeavesEveryAudienceUntouched(string flag, string value)
    {
        await AddNotionAsync("old-secret");
        ConfigureOperatorState("notion");
        var toolsBefore = ReadConfig()["Tools"]!.ToJsonString();

        var args = new List<string> { "mcp", "add", "--transport", "http", "--client-id", "client", flag, value };
        if (flag != "--client-secret")
            args.AddRange(["--client-secret", "old-secret"]);

        args.AddRange(["notion", "https://changed.example.test/mcp"]);
        Assert.Equal(0, await McpCommand.RunAsync([.. args], _paths, output: _output));

        var config = ReadConfig();
        Assert.Equal("https://changed.example.test/mcp", config["McpServers"]!["notion"]!["Url"]!.GetValue<string>());
        Assert.Equal(toolsBefore, config["Tools"]!.ToJsonString());
        Assert.Contains("Updated MCP server 'notion'", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("unchanged", _output.ToString(), StringComparison.Ordinal);

        var loaded = McpCommand.LoadMcpServers(_paths)["notion"];
        if (flag == "--client-secret")
            Assert.Equal(value, loaded.OAuthClientSecret?.Value);
        else
            Assert.Equal(value["X-Api-Key: ".Length..], loaded.Headers?["X-Api-Key"].Value);
    }

    [Fact]
    public async Task Add_ExistingServer_KeepsItDisabledAndIgnoresGrantAll()
    {
        await AddNotionAsync(clientSecret: null);
        ConfigureOperatorState("notion");
        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "disable", "notion"], _paths, output: _output));
        var toolsBefore = ReadConfig()["Tools"]!.ToJsonString();

        Assert.Equal(0, await McpCommand.RunAsync(
            ["mcp", "add", "--grant-all", "--transport", "http", "notion", "https://changed.example.test/mcp"],
            _paths,
            output: _output));

        var config = ReadConfig();
        Assert.False(config["McpServers"]!["notion"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(toolsBefore, config["Tools"]!.ToJsonString());
        Assert.Contains("--grant-all ignored", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_ClearsEverythingKeyedByTheServer_AndReAddStartsClean()
    {
        await AddNotionAsync("client-secret");
        Assert.Equal(0, await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "http", "other", "https://other.example.test/mcp"],
            _paths,
            output: _output));
        ConfigureOperatorState("notion");
        ConfigureOperatorState("other");
        await SeedOAuthTokensAsync(Notion);
        await SeedOAuthTokensAsync(new McpServerName("other"));
        SeedApprovals();

        var otherConfigBefore = OtherServerConfig();
        Assert.True(NewCredentialStore().HasAnyActive(Notion));

        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "remove", "notion"], _paths, output: _output));

        // Nothing keyed by the name remains in either file, the token store or the approval store.
        Assert.DoesNotContain("notion", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
        Assert.DoesNotContain("notion", File.ReadAllText(_paths.SecretsPath), StringComparison.Ordinal);
        Assert.False(NewCredentialStore().HasAnyActive(Notion));
        var approvals = new ToolApprovalStore(_paths.ToolApprovalsPath).Snapshot();
        Assert.DoesNotContain(approvals.Values.SelectMany(tools => tools.Keys), tool => tool.StartsWith("notion/", StringComparison.Ordinal));

        // Other servers and first-party approvals are untouched.
        Assert.Equal(otherConfigBefore, OtherServerConfig());
        Assert.True(NewCredentialStore().HasAnyActive(new McpServerName("other")));
        Assert.Contains("other/search", approvals["team"].Keys);
        Assert.Contains("shell_execute", approvals["personal"].Keys);

        // Adding the name again signs in from scratch and gets the secure defaults.
        Assert.Equal(0, await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "http", "notion", Url],
            _paths,
            output: _output));
        Assert.True(NewCredentialStore().RequiresAuthorization(Notion, Url));
        var team = ReadConfig()["Tools"]!["AudienceProfiles"]!["Team"]!;
        Assert.Empty(team["McpServerToolGrants"]!["notion"]!.AsArray());
        Assert.DoesNotContain("notion", team["AllowedMcpServers"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal("Approval", team["ApprovalPolicy"]!["McpServerDefaults"]!["notion"]!.GetValue<string>());
        Assert.DoesNotContain(
            team["ApprovalPolicy"]!["ToolOverrides"]!.AsObject().Select(pair => pair.Key),
            key => key.StartsWith("notion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_ServerAbsentFromMcpServers_StillClearsLeftoverAudienceEntries()
    {
        await AddNotionAsync(clientSecret: null);
        ConfigureOperatorState("notion");
        var config = ReadConfig();
        config["McpServers"]!.AsObject().Remove("notion");
        File.WriteAllText(_paths.NetclawConfigPath, config.ToJsonString());

        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "remove", "notion"], _paths, output: _output));

        Assert.DoesNotContain("notion", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
    }

    private Task AddNotionAsync(string? clientSecret)
    {
        var args = new List<string> { "mcp", "add", "--transport", "http", "--client-id", "client" };
        if (clientSecret is not null)
            args.AddRange(["--client-secret", clientSecret]);

        args.AddRange(["notion", Url]);
        return McpCommand.RunAsync([.. args], _paths, output: _output);
    }

    // The state an operator builds after add: grants, allow-list entries, approval modes and
    // per-tool overrides, with different values for Team and Public.
    private void ConfigureOperatorState(string server)
    {
        var config = ReadConfig();
        var profiles = config["Tools"]!["AudienceProfiles"]!.AsObject();
        foreach (var (audience, grants, mode, overrideMode) in new[]
                 {
                     ("Team", new[] { "search", "fetch" }, "Auto", "Deny"),
                     ("Public", new[] { "search" }, "Approval", "Auto"),
                 })
        {
            var profile = profiles[audience]!.AsObject();
            profile["McpServersMode"] = "Allowlist";
            profile["AllowedMcpServers"] = new JsonArray("unrelated", server.ToUpperInvariant(), server);
            profile["McpServerToolGrants"]![server] = new JsonArray([.. grants.Select(tool => (JsonNode)tool)]);
            var policy = profile["ApprovalPolicy"]!.AsObject();
            policy["McpServerDefaults"]![server] = mode;
            policy["ToolOverrides"] = new JsonObject
            {
                [$"{server}/fetch"] = overrideMode,
                [$"{server}__search"] = overrideMode,
                ["file_write"] = "Deny",
            };
        }

        File.WriteAllText(_paths.NetclawConfigPath, config.ToJsonString());
    }

    private void SeedApprovals()
    {
        var store = new ToolApprovalStore(_paths.ToolApprovalsPath);
        store.AddApproval(TrustAudience.Team, "notion/fetch", ApprovalEntry.CreateNonShell("notion/fetch"));
        store.AddApproval(TrustAudience.Public, "notion/search", ApprovalEntry.CreateNonShell("notion/search"));
        store.AddApproval(TrustAudience.Team, "other/search", ApprovalEntry.CreateNonShell("other/search"));
        store.AddApproval(
            TrustAudience.Personal,
            "shell_execute",
            ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["git", "push"]));
    }

    private async Task SeedOAuthTokensAsync(McpServerName server)
    {
        var store = NewCredentialStore();
        var cache = store.CreateTokenCache(
            server,
            Url,
            new McpOAuthClientIdentity("client", clientSecret: null, dynamicClientRegistration: false),
            explicitAuthorization: true);
        await cache.StoreTokensAsync(
            new TokenContainer
            {
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                TokenType = "Bearer",
                ExpiresIn = 3600,
                ObtainedAt = DateTimeOffset.UtcNow,
            },
            TestContext.Current.CancellationToken);
        store.Publish(cache, TestContext.Current.CancellationToken);
    }

    private McpOAuthCredentialStore NewCredentialStore() => new(
        _paths,
        TimeProvider.System,
        SecretsProtection.CreateProtector(_paths),
        NullLogger<McpOAuthCredentialStore>.Instance);

    private string OtherServerConfig()
    {
        var config = ReadConfig();
        var profiles = config["Tools"]!["AudienceProfiles"]!;
        return string.Join(
            "|",
            config["McpServers"]!["other"]!.ToJsonString(),
            string.Join(
                ",",
                new[] { "Personal", "Team", "Public" }.Select(audience => string.Join(
                    ";",
                    profiles[audience]!["McpServerToolGrants"]?["other"]?.ToJsonString(),
                    profiles[audience]!["ApprovalPolicy"]!["McpServerDefaults"]!["other"]!.ToJsonString(),
                    profiles[audience]!["ApprovalPolicy"]!["ToolOverrides"]?["other/fetch"]?.ToJsonString()))));
    }

    private JsonNode ReadConfig() => JsonNode.Parse(File.ReadAllText(_paths.NetclawConfigPath))!;
}
