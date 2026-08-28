// -----------------------------------------------------------------------
// <copyright file="WorktreeCreateToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;

namespace Netclaw.Actors.Tests.Tools;

public sealed class WorktreeCreateToolTests : IDisposable
{
    private readonly DisposableTempDir _directory = new();
    private readonly NetclawPaths _paths;
    private readonly SessionStoragePaths _storage;
    private readonly string _repository;

    public WorktreeCreateToolTests()
    {
        _paths = new NetclawPaths(_directory.Path);
        _paths.EnsureDirectoriesExist();
        _storage = SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(
            Path.GetFullPath(Path.Combine(_paths.SessionsDirectory, "session-1"))));
        _repository = Path.Combine(_storage.SessionDirectory, "source");
        Directory.CreateDirectory(Path.Combine(_repository, ".git"));
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Success_uses_argument_array_and_returns_a_project_effect()
    {
        var processFactory = new RecordingGitProcessFactory(exitCode: 0, output: "prepared");
        var tool = CreateTool(processFactory);
        var context = CreateContext();

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Branch", "Feature/Session Storage"),
            context.Invocation,
            TestContext.Current.CancellationToken);

        Assert.Contains("Managed worktree created.", result, StringComparison.Ordinal);
        Assert.Equal(_repository, processFactory.WorkingDirectory);
        Assert.Equal("worktree", processFactory.Arguments[0]);
        Assert.Equal("add", processFactory.Arguments[1]);
        Assert.Equal("-b", processFactory.Arguments[2]);
        Assert.Equal("Feature/Session Storage", processFactory.Arguments[3]);
        var destination = processFactory.Arguments[4];
        Assert.StartsWith(_storage.WorktreeDirectory, destination, StringComparison.Ordinal);
        Assert.Equal(destination, context.Receipt?.DeclaredProjectDirectory);
        Assert.Equal(ToolInvocationOutcomeCategory.Success, context.Receipt?.Category);
        Assert.True(File.Exists(Path.Combine(
            _storage.WorktreeDirectory,
            ".ownership",
            $"{Path.GetFileName(destination)}.json")));
    }

    [Fact]
    public async Task Failure_removes_only_the_empty_reservation_and_has_no_project_effect()
    {
        var processFactory = new RecordingGitProcessFactory(exitCode: 1, error: "branch exists");
        var tool = CreateTool(processFactory);
        var context = CreateContext();

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Branch", "existing"),
            context.Invocation,
            TestContext.Current.CancellationToken);

        Assert.Contains("branch exists", result, StringComparison.Ordinal);
        Assert.False(Directory.Exists(processFactory.Arguments[4]));
        Assert.Null(context.Receipt?.DeclaredProjectDirectory);
        Assert.Equal(ToolInvocationOutcomeCategory.TransientFailure, context.Receipt?.Category);
    }

    [Fact]
    public void Schema_does_not_accept_a_destination()
    {
        var properties = CreateTool(new RecordingGitProcessFactory(0))
            .ParameterSchema
            .GetProperty("properties");

        Assert.True(properties.TryGetProperty("Branch", out _));
        Assert.True(properties.TryGetProperty("SourceRepository", out _));
        Assert.False(properties.TryGetProperty("Destination", out _));
    }

    private WorktreeCreateTool CreateTool(IGitProcessFactory processFactory)
        => new(
            new ToolConfig(),
            _paths,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-08-28T00:00:00Z")),
            processFactory);

    private ToolExecutionContext CreateContext()
        => TestToolExecutionContext.CreateBoundWithStorage(
            "signalr/session-1",
            _storage,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal,
                ChannelType = "signalr",
                ProjectDirectory = _repository
            });

    private sealed class RecordingGitProcessFactory(
        int exitCode,
        string output = "",
        string error = "") : IGitProcessFactory
    {
        public string WorkingDirectory { get; private set; } = string.Empty;
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public IRunningGitProcess Start(
            string workingDirectory,
            IReadOnlyList<string> arguments)
        {
            WorkingDirectory = workingDirectory;
            Arguments = arguments.ToArray();
            return new CompletedGitProcess(exitCode, output, error);
        }
    }

    private sealed class CompletedGitProcess(
        int exitCode,
        string output,
        string error) : IRunningGitProcess
    {
        public int ExitCode => exitCode;
        public Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken)
            => Task.FromResult(output);
        public Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken)
            => Task.FromResult(error);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public bool TryKillTree() => true;
        public void Dispose()
        {
        }
    }
}
