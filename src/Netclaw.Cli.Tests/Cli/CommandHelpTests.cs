// -----------------------------------------------------------------------
// <copyright file="CommandHelpTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Approvals;
using Netclaw.Cli.Mcp;
using Netclaw.Cli.Memory;
using Netclaw.Cli.Model;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Reminder;
using Netclaw.Cli.Secrets;
using Netclaw.Cli.Skills;
using Netclaw.Cli.Webhooks;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// <c>&lt;group&gt; &lt;subcommand&gt; --help</c> must print usage and stop. A dispatcher that
/// checks only the subcommand slot reads the help token as a name or a flag, and can run the
/// subcommand on it (<c>mcp add --help</c> could create a server called <c>--help</c>).
/// </summary>
public sealed class CommandHelpTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public CommandHelpTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string> Subcommands() =>
    [
        "mcp add", "mcp auth", "mcp list", "mcp get", "mcp remove", "mcp enable", "mcp disable",
        "provider list", "provider add", "provider rename", "provider remove",
        "model list", "model set", "model discover", "model clear",
        "approvals list", "approvals revoke", "approvals trust-verb",
        "skill list", "skill sync", "skill show", "skill validate", "skill remove", "skill issues",
        "skill search", "skill source list", "skill source add", "skill source remove",
        "skill source enable", "skill source disable",
        "memory backfill-embeddings",
        "webhooks list", "webhooks show", "webhooks set", "webhooks delete", "webhooks validate",
        "reminder list", "reminder create", "reminder cancel", "reminder delete", "reminder disable",
        "reminder enable", "reminder import", "reminder validate", "reminder show", "reminder history",
        "reminder status", "reminder run",
        "secrets set", "secrets add",
    ];

    [Theory]
    [MemberData(nameof(Subcommands))]
    public async Task HelpFlag_AfterSubcommand_PrintsUsageAndChangesNothing(string commandLine)
    {
        foreach (var flag in new[] { "--help", "-h" })
        {
            string[] args = [.. commandLine.Split(' '), flag];
            var before = Snapshot();
            using var output = new StringWriter();

            var exitCode = await RunAsync(args, output);

            Assert.Equal(0, exitCode);
            Assert.StartsWith($"Usage: netclaw {args[0]}", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(before, Snapshot());
        }
    }

    [Theory]
    [InlineData("add")]
    [InlineData("auth")]
    [InlineData("get")]
    [InlineData("remove")]
    [InlineData("enable")]
    [InlineData("disable")]
    public async Task McpServerName_StartingWithDash_IsRejected(string subcommand)
    {
        // `add` gets a URL as well, so only the name guard stops it from creating the server.
        string[] args = subcommand == "add"
            ? ["mcp", "add", "--transport", "http", "-x", "https://mcp.example.test/mcp"]
            : ["mcp", subcommand, "-x"];
        using var output = new StringWriter();

        var exitCode = await McpCommand.RunAsync(args, _paths, output: output);

        Assert.Equal(1, exitCode);
        Assert.Contains("must not start with '-'", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task McpAdd_ArgumentsAfterDoubleDash_AreNotHelpRequests()
    {
        using var output = new StringWriter();

        var exitCode = await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "stdio", "tool", "--", "server", "--help"],
            _paths,
            output: output);

        Assert.Equal(0, exitCode);
        Assert.Contains("Added MCP server 'tool'", output.ToString(), StringComparison.Ordinal);
    }

    private Task<int> RunAsync(string[] args, StringWriter output)
    {
        using var error = new StringWriter();
        var configuration = new ConfigurationBuilder().Build();
        return args[0] switch
        {
            "mcp" => McpCommand.RunAsync(args, _paths, output: output),
            "provider" => ProviderCommand.RunAsync(args, _paths, output: output),
            "model" => ModelCommand.RunAsync(args, _paths, output: output),
            "approvals" => ApprovalsCommand.RunAsync(args, _paths, output, diagnostics: error),
            "skill" => SkillCommand.RunAsync(args, _paths, output: output),
            "memory" => MemoryCommand.RunAsync(args, _paths, configuration, output, error),
            "webhooks" => WebhooksCommand.RunAsync(args, _paths, output, error: error),
            "reminder" => ReminderCommand.RunAsync(args, daemonApi: null, output, error),
            "secrets" => Task.FromResult(SecretsCommand.Run(args, _paths, output)),
            _ => throw new ArgumentOutOfRangeException(nameof(args), args[0], "Unknown command group."),
        };
    }

    private string Snapshot() => string.Join(
        "\n",
        Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(file => $"{file}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}"));
}
