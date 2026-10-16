// -----------------------------------------------------------------------
// <copyright file="UnixOwnershipTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tests.Cli;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Daemon;

public sealed class UnixOwnershipTests : IDisposable
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [SlopwatchSuppress("SW001", "The owner lookup is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "The owner lookup is Linux-only.")]
    public void CurrentUserId_is_the_user_id_that_the_id_command_reports()
        => Assert.Equal(ReadUserIdFromIdCommand(), UnixOwnership.CurrentUserId());

    [SlopwatchSuppress("SW001", "The owner lookup is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "The owner lookup is Linux-only.")]
    public void TryGetOwnerId_reads_the_owner_of_a_file_that_this_user_made()
    {
        var file = Path.Combine(_dir.Path, "mine");
        File.WriteAllText(file, "x");

        Assert.True(UnixOwnership.TryGetOwnerId(file, out var owner));
        Assert.Equal(ReadUserIdFromIdCommand(), owner);
    }

    [SlopwatchSuppress("SW001", "The owner lookup is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "The owner lookup is Linux-only.")]
    public void TryGetOwnerId_follows_a_link_to_the_owner_of_its_target()
    {
        var link = Path.Combine(_dir.Path, "link");
        File.CreateSymbolicLink(link, "/etc/passwd");

        Assert.True(UnixOwnership.TryGetOwnerId(link, out var owner));
        Assert.Equal(0u, owner);
    }

    [SlopwatchSuppress("SW001", "The owner lookup is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "The owner lookup is Linux-only.")]
    public void TryGetOwnerId_is_false_for_a_path_that_does_not_exist()
        => Assert.False(UnixOwnership.TryGetOwnerId(Path.Combine(_dir.Path, "missing"), out _));

    private static uint ReadUserIdFromIdCommand()
    {
        using var process = Process.Start(new ProcessStartInfo("id", "-u") { RedirectStandardOutput = true })
            ?? throw new InvalidOperationException("The id command did not start.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return uint.Parse(output.Trim());
    }
}
