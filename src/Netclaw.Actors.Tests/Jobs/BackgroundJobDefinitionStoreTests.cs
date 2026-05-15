// -----------------------------------------------------------------------
// <copyright file="BackgroundJobDefinitionStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Jobs;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Jobs;

public sealed class BackgroundJobDefinitionStoreTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-job-store-tests-{Guid.NewGuid():N}");
    private readonly NetclawPaths _paths;

    public BackgroundJobDefinitionStoreTests()
    {
        _paths = new NetclawPaths(_basePath);
        _paths.EnsureDirectoriesExist();
    }

    /// <summary>
    /// Regression test for issue #994 legacy-document backfill.
    /// A pre-#994 background job document missing <c>audience</c> and <c>boundary</c> keys
    /// must load successfully with fail-closed Public defaults and must NOT be deleted.
    /// </summary>
    [Fact]
    public void Legacy_job_without_trust_fields_loads_with_public_audience()
    {
        // Authentic legacy shape: camelCase keys, enums as strings, no audience or boundary.
        var jobId = "legacy-job-001";
        var legacyJson = $$"""
            {
              "id": "{{jobId}}",
              "command": "make build",
              "sessionId": "C0TEST/1712000000.000001",
              "rationale": "Build the project artifacts.",
              "status": "Pending",
              "timeoutSeconds": 600,
              "startedAtMs": 0
            }
            """;

        var filePath = Path.Combine(_paths.JobsDirectory, $"{Uri.EscapeDataString(jobId)}.json");
        File.WriteAllText(filePath, legacyJson);

        var logger = new CapturingJobLogger<BackgroundJobDefinitionStore>();
        var store = new BackgroundJobDefinitionStore(_paths, logger);

        // Get by id — must load with Public defaults, not throw
        var byGet = store.Get(new BackgroundJobId(jobId));
        Assert.NotNull(byGet);
        Assert.Equal(TrustAudience.Public, byGet!.Audience);
        Assert.Equal(SecurityPolicyDefaults.PublicBoundary, byGet.Boundary);

        // All other fields must survive intact
        Assert.Equal(jobId, byGet.Id);
        Assert.Equal("make build", byGet.Command);
        Assert.Equal("C0TEST/1712000000.000001", byGet.SessionId);
        Assert.Equal("Build the project artifacts.", byGet.Rationale);
        Assert.Equal(BackgroundJobStatus.Pending, byGet.Status);
        Assert.Equal(600, byGet.TimeoutSeconds);

        // List must also surface the backfilled job
        var listed = store.List();
        Assert.Single(listed);
        Assert.Equal(jobId, listed[0].Id);
        Assert.Equal(TrustAudience.Public, listed[0].Audience);
        Assert.Equal(SecurityPolicyDefaults.PublicBoundary, listed[0].Boundary);

        // A warning must have been logged for the backfill operation
        Assert.NotEmpty(logger.Warnings);
        Assert.Contains(logger.Warnings, w => w.Contains(jobId) || w.Contains("audience"));
    }

    /// <summary>
    /// Positive control: a current document with explicit Audience and Boundary round-trips
    /// correctly through a fresh store instance (Save then re-read).
    /// </summary>
    [Fact]
    public void Current_job_with_trust_fields_roundtrips_exact_values()
    {
        var store = new BackgroundJobDefinitionStore(_paths);
        var jobId = "roundtrip-job-001";

        store.Save(new BackgroundJobDefinition
        {
            Id = jobId,
            Command = "dotnet test",
            SessionId = "C0ABC/1712000000.000001",
            Rationale = "Run the test suite.",
            Status = BackgroundJobStatus.Pending,
            TimeoutSeconds = 300,
            Audience = TrustAudience.Team,
            Boundary = SecurityPolicyDefaults.TeamBoundary,
            OriginChannelType = Netclaw.Actors.Channels.ChannelType.Slack
        });

        // Re-open from a fresh store instance to exercise deserialization
        var freshStore = new BackgroundJobDefinitionStore(_paths);
        var loaded = freshStore.Get(new BackgroundJobId(jobId));

        Assert.NotNull(loaded);
        Assert.Equal(TrustAudience.Team, loaded!.Audience);
        Assert.Equal(SecurityPolicyDefaults.TeamBoundary, loaded.Boundary);
        Assert.Equal(jobId, loaded.Id);
        Assert.Equal("dotnet test", loaded.Command);
        Assert.Equal("C0ABC/1712000000.000001", loaded.SessionId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }
}

/// <summary>
/// Capturing <see cref="ILogger{T}"/> that records formatted warning messages.
/// Used to verify the legacy-document backfill warning is emitted on read.
/// </summary>
internal sealed class CapturingJobLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
            Warnings.Add(formatter(state, exception));
    }
}
