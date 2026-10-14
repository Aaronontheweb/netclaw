// -----------------------------------------------------------------------
// <copyright file="SystemdUserServiceHomeGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class SystemdUserServiceHomeGuardTests
{
    // The stop rule compares the unit against the home the CLI was started for. A
    // construction that names any other home (or the default) judges the wrong daemon.
    private static readonly Regex Construction = new(
        @"new\s+SystemdUserService\s*\(\s*(?<home>[^,)\r\n]*)",
        RegexOptions.Compiled);

    [Fact]
    public void Every_production_SystemdUserService_is_built_for_the_injected_home()
    {
        var cliDir = Path.Combine(FindRepoRoot(), "src", "Netclaw.Cli");
        var offenders = Directory.EnumerateFiles(cliDir, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => Violations(File.ReadAllText(path)).Select(home => $"{Path.GetRelativePath(cliDir, path)}: {home}"))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Build SystemdUserService with the injected paths' home (paths.BasePath): " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("new SystemdUserService(paths.BasePath)", false)]
    [InlineData("new SystemdUserService(_daemonManager.HomePath)", false)]
    [InlineData("new SystemdUserService(SystemdUserService.DefaultHomePath)", true)]
    [InlineData("new SystemdUserService(new NetclawPaths().BasePath)", true)]
    public void The_scan_flags_a_construction_that_does_not_use_the_injected_home(string source, bool expected)
        => Assert.Equal(expected, Violations(source).Any());

    private static IEnumerable<string> Violations(string source)
        => Construction.Matches(source)
            .Select(static m => m.Groups["home"].Value.Trim())
            .Where(static home => !(home.EndsWith(".BasePath", StringComparison.Ordinal) || home.EndsWith(".HomePath", StringComparison.Ordinal))
                || home.StartsWith("new ", StringComparison.Ordinal)
                || home.StartsWith("SystemdUserService.", StringComparison.Ordinal));

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
