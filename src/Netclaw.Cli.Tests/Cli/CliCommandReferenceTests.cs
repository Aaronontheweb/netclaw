// -----------------------------------------------------------------------
// <copyright file="CliCommandReferenceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class CliCommandReferenceTests
{
    // Messages that tell the user to run `netclaw run` or `netclaw provider fix` shipped
    // because nothing checked them against the commands the CLI really has. This scans the
    // string literals of every shipped project for a `netclaw <command> [<subcommand>]`
    // reference (in backticks or quotes, in parentheses, or after "Run: " / "Usage: ").
    // The top-level command must be in CliArgsParser.KnownCommands. The subcommand must
    // appear as a quoted literal somewhere in the CLI source, which is where each command
    // dispatches on it.
    private static readonly Regex StringLiteral = new("\"(?:\\\\.|[^\"\\\\\\r\\n])*\"", RegexOptions.Compiled);

    private static readonly Regex CommandReference = new(
        @"(?:[`'(]|Run: |Usage: )netclaw (?<command>[a-z][a-z0-9-]*)(?: (?<sub>[a-z][a-z0-9-]*))?",
        RegexOptions.Compiled);

    [Fact]
    public void User_facing_messages_only_name_commands_the_cli_has()
    {
        var srcDir = Path.Combine(FindRepoRoot(), "src");
        var cliSource = string.Concat(SourceFiles(Path.Combine(srcDir, "Netclaw.Cli")).Select(File.ReadAllText));
        var subcommands = StringLiteral.Matches(cliSource)
            .Select(static m => m.Value.Trim('"'))
            .ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();
        foreach (var file in SourceFiles(srcDir))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                foreach (Match literal in StringLiteral.Matches(line))
                {
                    foreach (Match reference in CommandReference.Matches(literal.Value))
                    {
                        var command = reference.Groups["command"].Value;
                        var sub = reference.Groups["sub"].Value;
                        var known = CliArgsParser.KnownCommands.Contains(command)
                            && (sub.Length == 0 || subcommands.Contains(sub));
                        if (!known)
                            offenders.Add($"{Path.GetRelativePath(srcDir, file)}:{lineNumber} netclaw {command} {sub}".TrimEnd());
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These messages name a netclaw command the CLI does not have:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                && !path.Contains("Tests", StringComparison.Ordinal)
                && !path.Contains("SmokeLlmServer", StringComparison.Ordinal));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IMPLEMENTATION_PLAN.md")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
