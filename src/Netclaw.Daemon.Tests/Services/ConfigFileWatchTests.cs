// -----------------------------------------------------------------------
// <copyright file="ConfigFileWatchTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ConfigFileWatchTests
{
    [Theory]
    [InlineData("no_event", "", "", false, false, 0)]
    [InlineData("before_attach_changed", "changed", "", false, false, 1)]
    [InlineData("before_attach_created", "created", "", false, false, 1)]
    [InlineData("coalesced_before_attach", "changed,created,rename_into", "", false, false, 1)]
    [InlineData("after_attach", "", "changed", false, false, 1)]
    [InlineData("rename_into", "rename_into", "", false, false, 1)]
    [InlineData("deleted_only", "deleted", "", false, false, 0)]
    [InlineData("rename_away", "rename_away", "", false, false, 0)]
    [InlineData("secrets_only", "secrets", "secrets", false, false, 0)]
    [InlineData("change_then_delete", "changed,deleted", "", false, false, 1)]
    [InlineData("change_then_rename_away", "changed,rename_away", "", false, false, 1)]
    [InlineData("other_file", "other", "other", false, false, 0)]
    [InlineData("detached_pending", "changed", "", true, false, 0)]
    [InlineData("event_after_detach", "", "changed", false, true, 0)]
    public void Native_events_preserve_the_handoff_contract(
        string scenario, string beforeAttach, string afterAttach,
        bool detachBeforeAttach, bool detachAfterAttach, int expectedReloads)
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        using var watch = new ConfigFileWatch(paths);
        var reloads = 0;

        Dispatch(watch, paths, beforeAttach);
        if (detachBeforeAttach)
        {
            watch.Detach();
            Assert.ThrowsAny<InvalidOperationException>(() =>
                watch.Attach(() => reloads++, NullLogger<ConfigWatcherService>.Instance));
        }
        else
        {
            watch.Attach(() => reloads++, NullLogger<ConfigWatcherService>.Instance);
        }

        if (detachAfterAttach)
            watch.Detach();
        Dispatch(watch, paths, afterAttach);

        // Attach and the handlers return after each synchronous callback completes.
        Assert.True(reloads == expectedReloads,
            $"The {scenario} case expected {expectedReloads} reloads and observed {reloads}.");
    }

    [Fact]
    public void Duplicate_attach_fails_and_keeps_the_original_consumer()
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        using var watch = new ConfigFileWatch(paths);
        var originalReloads = 0;
        var replacementReloads = 0;
        watch.Attach(() => originalReloads++, NullLogger<ConfigWatcherService>.Instance);

        Assert.ThrowsAny<InvalidOperationException>(() =>
            watch.Attach(() => replacementReloads++, NullLogger<ConfigWatcherService>.Instance));
        Dispatch(watch, paths, "changed");

        Assert.Equal(1, originalReloads);
        Assert.Equal(0, replacementReloads);
    }

    private static void Dispatch(ConfigFileWatch watch, NetclawPaths paths, string events)
    {
        foreach (var name in events.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (name)
            {
                case "changed":
                case "created":
                case "secrets":
                case "other":
                    var file = name switch
                    {
                        "secrets" => "secrets.json",
                        "other" => "random.txt",
                        _ => "netclaw.json"
                    };
                    var type = name == "created" ? WatcherChangeTypes.Created : WatcherChangeTypes.Changed;
                    watch.OnFileChanged(watch, new FileSystemEventArgs(type, paths.ConfigDirectory, file));
                    break;
                case "rename_into":
                    watch.OnFileRenamed(watch, new RenamedEventArgs(
                        WatcherChangeTypes.Renamed, paths.ConfigDirectory, "netclaw.json", "netclaw.json.tmp"));
                    break;
                case "deleted":
                    watch.OnFileDeleted(watch, new FileSystemEventArgs(
                        WatcherChangeTypes.Deleted, paths.ConfigDirectory, "netclaw.json"));
                    break;
                case "rename_away":
                    watch.OnFileRenamed(watch, new RenamedEventArgs(
                        WatcherChangeTypes.Renamed, paths.ConfigDirectory, "netclaw.json.old", "netclaw.json"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(events), name, "Unknown file event.");
            }
        }
    }
}
