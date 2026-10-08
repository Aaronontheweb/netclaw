// -----------------------------------------------------------------------
// <copyright file="CliContextGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class CliContextGuardTests
{
    // This guard covers only the adopted commands. Program owns process composition.
    private static readonly Regex ProcessDefault = new(
        @"\b(?:Console\s*\.|TimeProvider\s*\.\s*System\b|DateTime(?:Offset)?\s*\.\s*(?:UtcNow|Now)\b|new\s+NetclawPaths\s*\(\s*\)|new\s+SystemdUserService\s*\(\s*\))",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("Console.Error.WriteLine(message);", true)]
    [InlineData("System.Console.Out.WriteLine(message);", true)]
    [InlineData("Console.ReadLine();", true)]
    [InlineData("TimeProvider . System", true)]
    [InlineData("DateTimeOffset.UtcNow", true)]
    [InlineData("DateTime.Now", true)]
    [InlineData("new NetclawPaths ( )", true)]
    [InlineData("new SystemdUserService()", true)]
    [InlineData("cli.Output.WriteLine(message);", false)]
    [InlineData("cli.Time.GetUtcNow()", false)]
    [InlineData("new NetclawPaths(home)", false)]
    [InlineData("new SystemdUserService(homePath: paths.BasePath)", false)]
    public void Guard_recognizes_process_defaults(string source, bool expected)
    {
        Assert.Equal(expected, ProcessDefault.IsMatch(source));
    }

    [Theory]
    [InlineData("Daemon/PairCommand.cs")]
    [InlineData("Approvals/ApprovalsCommand.cs")]
    [InlineData("Update/UpdateCommand.cs")]
    public void Adopted_commands_do_not_select_process_defaults(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IMPLEMENTATION_PLAN.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "src", "Netclaw.Cli", relativePath);
        var source = File.ReadAllText(path);

        // The startup notice uses stderr after the TUI closes. It is not command output.
        if (relativePath == "Update/UpdateCommand.cs")
            source = source.Replace("Console.Error.WriteLine(notice);", string.Empty, StringComparison.Ordinal);

        Assert.False(ProcessDefault.IsMatch(source), $"{relativePath} must use its supplied CLI environment.");
    }
}
