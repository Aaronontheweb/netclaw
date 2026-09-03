// -----------------------------------------------------------------------
// <copyright file="SessionStorageFileAccessPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;

namespace Netclaw.Actors.Tests.Tools;

public sealed class SessionStorageFileAccessPolicyTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();
    private readonly NetclawPaths _paths;
    private readonly SessionStoragePaths _storage;
    private readonly ScopedFileAccessPolicy _policy;
    private readonly ToolInvocationContext _context;

    public SessionStorageFileAccessPolicyTests()
    {
        _paths = new NetclawPaths(_directory.Path);
        _paths.EnsureDirectoriesExist();
        var envelope = Path.Combine(_paths.SessionsDirectory, "current-session");
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.GetFullPath(envelope)));
        _policy = new ScopedFileAccessPolicy(new ToolConfig(), _paths);
        _context = TestToolExecutionContext.CreateBoundWithStorage(
            "signalr/current-session",
            _storage,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal,
                ChannelType = "signalr"
            }).Invocation;
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Current_parent_and_child_logs_follow_normal_operation_permissions()
    {
        var child = _storage.ForChild(
            new SubAgentRunId("run-1"),
            new SubAgentScopeId("signalr/current-session/subagent/test/run-1"));

        Assert.True(_policy.TryResolveReadPath(_storage.LogPath, _context, out _, out _));
        Assert.True(_policy.TryResolveReadPath(child.LogPath, _context, out _, out _));
        Assert.True(_policy.TryResolveWritePath(_storage.LogPath, _context, out _, out _));
        Assert.True(_policy.TryResolveAttachPath(child.LogPath, _context, out _, out _));
    }

    [Fact]
    public void Unrestricted_interactive_personal_profile_uses_ordinary_foreign_path_access()
    {
        var foreignMain = Path.Combine(
            _paths.SessionsDirectory,
            "foreign-session",
            "logs",
            "session.log");
        var foreignChild = Path.Combine(
            _paths.SessionsDirectory,
            "foreign-session",
            "subagents",
            "run-2",
            "logs",
            "session.log");

        Assert.True(_policy.TryResolveReadPath(foreignMain, _context, out _, out _));
        Assert.True(_policy.TryResolveReadPath(foreignChild, _context, out _, out _));
    }

    [Fact]
    public void Complete_current_session_envelope_is_one_ordinary_root()
    {
        var childArtifact = Path.Combine(
            _storage.Binding!.EnvelopeRoot.Value,
            "subagents",
            "run-1",
            "artifacts",
            "result.txt");
        var broadChildRoot = Path.Combine(
            _storage.Binding.EnvelopeRoot.Value,
            "subagents",
            "run-1");

        Assert.True(_policy.TryResolveWritePath(
            Path.Combine(_storage.ManagedTemporary.Directory, "result.txt"),
            _context,
            out _,
            out _));
        Assert.True(_policy.TryResolveReadPath(childArtifact, _context, out _, out _));
        Assert.True(_policy.TryResolveReadPath(broadChildRoot, _context, out _, out _));
        Assert.True(_policy.TryResolveReadPath(
            _storage.Binding.EnvelopeRoot.Value,
            _context,
            out _,
            out _));
    }

    [Fact]
    public async Task File_read_and_search_do_not_interrupt_an_active_log_writer()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storage.LogPath)!);
        await using var stream = new FileStream(
            _storage.LogPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };
        await writer.WriteLineAsync("active marker");

        var pathPolicy = new Netclaw.Security.ToolPathPolicy([]);
        var readTool = new FileReadTool(new ToolConfig(), _paths, pathPolicy);
        var searchTool = new FileSearchTool(new ToolConfig(), _paths, pathPolicy);
        var read = await readTool.ExecuteAsync(
            ToolInput.Create("Path", _storage.LogPath),
            _context,
            TestContext.Current.CancellationToken);
        var search = await searchTool.ExecuteAsync(
            ToolInput.Create(
                "Root", Path.GetDirectoryName(_storage.LogPath)!,
                "Query", "active marker",
                "Mode", "content"),
            _context,
            TestContext.Current.CancellationToken);

        Assert.Contains("active marker", read, StringComparison.Ordinal);
        Assert.Contains("active marker", search, StringComparison.Ordinal);
        await writer.WriteLineAsync("writer remains active");
        await writer.FlushAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Linked_session_root_does_not_grant_file_access()
    {
        var outside = Path.Combine(_directory.Path, "outside-envelope");
        var linkedEnvelope = Path.Combine(_paths.SessionsDirectory, "linked-envelope");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(linkedEnvelope, outside);
        var storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.GetFullPath(linkedEnvelope)));
        var context = TestToolExecutionContext.CreateBoundWithStorage(
            "signalr/linked-session",
            storage,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.Team,
                ChannelType = "signalr"
            }).Invocation;

        var allowed = _policy.TryResolveReadPath(
            Path.Combine(linkedEnvelope, "logs", "session.log"),
            context,
            out _,
            out var error);

        Assert.False(allowed);
        Assert.Contains("symlinked paths", error, StringComparison.Ordinal);
    }
}
