// -----------------------------------------------------------------------
// <copyright file="SafeFileSystemMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class SafeFileSystemMutationTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), $"netclaw-safe-fs-{Guid.NewGuid():N}");
    private readonly string _root;

    public SafeFileSystemMutationTests()
    {
        _root = Path.Combine(_parent, "root");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_parent))
            Directory.Delete(_parent, recursive: true);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("inside/../../outside")]
    public void OpenDirectory_rejects_a_parent_segment_before_any_change(string relativePath)
    {
        // Callers pass Path.GetRelativePath output. A target outside the root gives
        // ".." segments, which must fail closed and must not create a directory.
        Assert.Throws<ArgumentException>(
            () => SafeFileSystemMutation.OpenDirectory(_root, relativePath, createMissing: true));

        Assert.False(Directory.Exists(Path.Combine(_parent, "outside")));
        Assert.False(Directory.Exists(Path.Combine(_root, "inside")));
    }

    [Fact]
    public void OpenDirectory_creates_missing_segments_only_when_asked()
    {
        Assert.Null(SafeFileSystemMutation.OpenDirectory(_root, "a/b", createMissing: false));
        Assert.False(Directory.Exists(Path.Combine(_root, "a")));

        using (var directory = SafeFileSystemMutation.OpenDirectory(_root, "a/b", createMissing: true))
        {
            Assert.NotNull(directory);
            directory.WriteAllTextAtomic("file.txt", "text", "file.txt.tmp");
        }

        Assert.Equal("text", File.ReadAllText(Path.Combine(_root, "a", "b", "file.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "a", "b", "file.txt.tmp")));
    }
}
