// -----------------------------------------------------------------------
// <copyright file="SafeVerbLoaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SafeVerbLoaderTests : IDisposable
{
    private readonly string _tempOverridePath = Path.Combine(
        Path.GetTempPath(),
        $"netclaw-safe-verbs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempOverridePath))
            File.Delete(_tempOverridePath);
    }

    [Fact]
    public void Load_returns_bundled_linux_defaults_when_no_override()
    {
        var list = SafeVerbLoader.Load(isWindows: false, overrideFilePath: null);

        // Spot-check a few entries from the spec's default Linux list.
        Assert.True(list.Contains("ls"));
        Assert.True(list.Contains("grep"));
        Assert.True(list.Contains("git status"));
        Assert.True(list.Contains("sed -n"));
        Assert.False(list.Contains("git push"));
        Assert.False(list.Contains("rm"));
    }

    [Fact]
    public void Load_returns_bundled_windows_defaults_when_no_override()
    {
        var list = SafeVerbLoader.Load(isWindows: true, overrideFilePath: null);

        // Spot-check a few entries from the spec's default Windows list.
        Assert.True(list.Contains("dir"));
        Assert.True(list.Contains("Get-Content"));
        Assert.True(list.Contains("Test-Path"));
        Assert.True(list.Contains("git status"));
        Assert.False(list.Contains("Remove-Item"));
    }

    [Fact]
    public void Load_user_override_extends_bundled_defaults()
    {
        File.WriteAllText(_tempOverridePath, """
            { "verbs": ["eza", "delta"] }
            """);

        var list = SafeVerbLoader.Load(isWindows: false, overrideFilePath: _tempOverridePath);

        // User additions present.
        Assert.True(list.Contains("eza"));
        Assert.True(list.Contains("delta"));
        // Bundled defaults remain.
        Assert.True(list.Contains("ls"));
        Assert.True(list.Contains("grep"));
    }

    [Fact]
    public void Load_user_override_cannot_remove_bundled_entries()
    {
        // Even if the user file is empty, the bundled defaults still apply.
        File.WriteAllText(_tempOverridePath, """{ "verbs": [] }""");

        var list = SafeVerbLoader.Load(isWindows: false, overrideFilePath: _tempOverridePath);

        Assert.True(list.Contains("ls"));
        Assert.True(list.Contains("grep"));
    }

    [Fact]
    public void Load_malformed_override_falls_back_to_bundled_defaults()
    {
        File.WriteAllText(_tempOverridePath, "not valid json {{{");

        var list = SafeVerbLoader.Load(isWindows: false, overrideFilePath: _tempOverridePath);

        // No throw; bundled defaults still loaded.
        Assert.True(list.Contains("ls"));
        Assert.True(list.Contains("grep"));
    }

    [Fact]
    public void Load_missing_override_path_uses_bundled_only()
    {
        var list = SafeVerbLoader.Load(isWindows: false, overrideFilePath: "/path/does/not/exist.json");

        Assert.True(list.Contains("ls"));
    }

    [Fact]
    public void Contains_uses_platform_correct_case_rules()
    {
        var list = SafeVerbLoader.Load(isWindows: false, overrideFilePath: null);

        if (OperatingSystem.IsWindows())
        {
            // OrdinalIgnoreCase
            Assert.True(list.Contains("LS"));
            Assert.True(list.Contains("ls"));
        }
        else
        {
            // Ordinal — `LS` is a different binary from `ls` on POSIX.
            Assert.False(list.Contains("LS"));
            Assert.True(list.Contains("ls"));
        }
    }
}
