// -----------------------------------------------------------------------
// <copyright file="AtomicFileTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class AtomicFileTests : IDisposable
{
    public static bool IsUnix => !OperatingSystem.IsWindows();

    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void WriteAllText_RoundTripsAndLeavesNoTempFile()
    {
        var path = Path.Combine(_dir.Path, "f.json");

        AtomicFile.WriteAllText(path, "payload");

        Assert.Equal("payload", File.ReadAllText(path));
        // A successful write leaves only the destination — no lingering .tmp-* sibling.
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public void WriteAllText_OverwritesExistingDestination()
    {
        var path = Path.Combine(_dir.Path, "f.json");
        AtomicFile.WriteAllText(path, "A");

        AtomicFile.WriteAllText(path, "B");

        Assert.Equal("B", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_FailureBeforeRename_LeavesPriorFileIntactAndCleansTemp()
    {
        var path = Path.Combine(_dir.Path, "f.json");
        File.WriteAllText(path, "ORIGINAL");

        // The harden callback runs after the temp is written but before the rename; throwing there
        // models any failure in that window. The destination must be untouched and the temp removed.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AtomicFile.WriteAllText(path, "NEW", _ => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", ex.Message);
        Assert.Equal("ORIGINAL", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp-*"));
    }

    [Fact]
    public void WriteAllText_HardensTempBeforeRename()
    {
        var path = Path.Combine(_dir.Path, "f.json");
        string? hardenedPath = null;
        var existedWhenHardened = false;

        AtomicFile.WriteAllText(path, "x", p =>
        {
            hardenedPath = p;
            existedWhenHardened = File.Exists(p);
        });

        Assert.NotNull(hardenedPath);
        Assert.True(existedWhenHardened);        // the temp existed when permissions were applied
        Assert.NotEqual(path, hardenedPath);     // perms applied to the temp, not the destination
        Assert.False(File.Exists(hardenedPath)); // the temp was renamed away afterward
    }

    [SlopwatchSuppress("SW001", "Unix file modes mean nothing on Windows.")]
    [Fact(SkipUnless = nameof(IsUnix), Skip = "Unix file modes mean nothing on Windows.")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void WriteAllText_with_a_callback_creates_the_temp_file_with_mode_0600()
    {
        var path = Path.Combine(_dir.Path, "owner-only.env");
        UnixFileMode? modeBeforeCallbackChanges = null;
        string? contentBeforeCallbackChanges = null;

        AtomicFile.WriteAllText(path, "plain=text\n", temp =>
        {
            modeBeforeCallbackChanges = File.GetUnixFileMode(temp);
            contentBeforeCallbackChanges = File.ReadAllText(temp);
            AtomicFile.HardenOwnerOnly(temp);
        });

        Assert.Equal("plain=text\n", contentBeforeCallbackChanges);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, modeBeforeCallbackChanges);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [SlopwatchSuppress("SW001", "Unix file modes mean nothing on Windows.")]
    [Fact(SkipUnless = nameof(IsUnix), Skip = "Unix file modes mean nothing on Windows.")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void WriteAllText_keeps_the_mode_that_the_callback_sets()
    {
        var path = Path.Combine(_dir.Path, "kept.json");
        File.WriteAllText(path, "old");
        var shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, shared);

        AtomicFile.WriteAllText(path, "new", temp => AtomicFile.CopyUnixMode(path, temp));

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(shared, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task WriteAllTextAsync_CancellationBeforeRename_LeavesPriorFileIntactAndCleansTemp()
    {
        var path = Path.Combine(_dir.Path, "f.json");
        File.WriteAllText(path, "ORIGINAL");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AtomicFile.WriteAllTextAsync(
                path,
                "NEW",
                temp =>
                {
                    Assert.Equal("NEW", File.ReadAllText(temp));
                    cancellation.Cancel();
                },
                cancellation.Token));

        Assert.Equal("ORIGINAL", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp-*"));
    }
}
