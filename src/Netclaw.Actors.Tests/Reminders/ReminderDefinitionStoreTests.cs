// -----------------------------------------------------------------------
// <copyright file="ReminderDefinitionStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

public sealed class ReminderDefinitionStoreTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-reminder-store-tests-{Guid.NewGuid():N}");
    private readonly NetclawPaths _paths;

    public ReminderDefinitionStoreTests()
    {
        _paths = new NetclawPaths(_basePath);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public void Constructor_prunes_invalid_json_and_records_dropped_definition()
    {
        var reminderId = "legacy-reminder";
        var filePath = Path.Combine(_paths.RemindersDirectory, $"{Uri.EscapeDataString(reminderId)}.json");
        File.WriteAllText(filePath, "{ this is invalid json }");

        var store = new ReminderDefinitionStore(_paths);

        Assert.False(File.Exists(filePath));

        var dropped = store.ConsumeDroppedInvalidDefinitions();
        var entry = Assert.Single(dropped);
        Assert.Equal(reminderId, entry.ReminderId);
        Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
        Assert.Empty(store.ConsumeDroppedInvalidDefinitions());
    }

    [Fact]
    public void Constructor_keeps_valid_definitions_while_pruning_invalid_files()
    {
        var seededStore = new ReminderDefinitionStore(_paths);
        var now = TimeProvider.System.GetUtcNow();

        seededStore.Save(new ReminderDefinition
        {
            Id = "valid-reminder",
            Title = "valid-reminder",
            Instructions = "check status",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddMinutes(30)
            },
            Audience = TrustAudience.Public,
            Boundary = SecurityPolicyDefaults.PublicBoundary,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        });

        var invalidPath = Path.Combine(_paths.RemindersDirectory, $"{Uri.EscapeDataString("bad-reminder")}.json");
        File.WriteAllText(invalidPath, "not json");

        var reloadedStore = new ReminderDefinitionStore(_paths);
        var reminders = reloadedStore.List();

        Assert.Single(reminders);
        Assert.Equal("valid-reminder", reminders[0].Id);
        Assert.False(File.Exists(invalidPath));
    }

    /// <summary>
    /// Regression test for issue #994 legacy-document backfill.
    /// A pre-#994 document missing <c>audience</c> and <c>boundary</c> keys must
    /// load successfully with fail-closed Public defaults, must NOT be deleted,
    /// and the store must log a warning naming the file.
    /// </summary>
    [Fact]
    public void Legacy_reminder_without_trust_fields_converts_on_read()
    {
        // Authentic legacy shape: camelCase keys, no audience or boundary, enums as strings.
        // FireAtMs is a long (unix milliseconds). CreatedAtMs and UpdatedAtMs are longs.
        const long fireAtMs = 1_800_000_000_000L; // some arbitrary future timestamp
        var reminderId = "legacy-no-trust";
        var legacyJson = $$"""
            {
              "id": "{{reminderId}}",
              "title": "Legacy Check",
              "schedule": {
                "type": "OneShot",
                "fireAtMs": {{fireAtMs}}
              },
              "instructions": "Check the build status.",
              "delivery": {
                "kind": "None"
              },
              "deliveryRequired": true,
              "deliveryInstructions": "Post result to channel.",
              "enabled": true,
              "createdBy": "alice",
              "createdAtMs": 1700000000000,
              "updatedAtMs": 1700000000000
            }
            """;

        var filePath = Path.Combine(_paths.RemindersDirectory, $"{Uri.EscapeDataString(reminderId)}.json");
        File.WriteAllText(filePath, legacyJson);

        var logger = new CapturingLogger<ReminderDefinitionStore>();
        var store = new ReminderDefinitionStore(_paths, logger);

        // The file must not be deleted — a legacy doc is not invalid, just old.
        Assert.True(File.Exists(filePath), "Legacy reminder file must NOT be pruned on load.");

        // Get by id
        var byGet = store.Get(new ReminderId(reminderId));
        Assert.NotNull(byGet);
        Assert.Equal(TrustAudience.Public, byGet!.Audience);
        Assert.Equal(SecurityPolicyDefaults.PublicBoundary, byGet.Boundary);

        // All other fields must survive intact
        Assert.Equal(reminderId, byGet.Id);
        Assert.Equal("Legacy Check", byGet.Title);
        Assert.Equal(ReminderScheduleType.OneShot, byGet.Schedule.Type);
        Assert.Equal(fireAtMs, byGet.Schedule.FireAtMs);
        Assert.Equal("Check the build status.", byGet.Instructions);
        Assert.Equal(DeliveryKind.None, byGet.Delivery.Kind);
        Assert.True(byGet.Enabled);
        Assert.Equal("alice", byGet.CreatedBy);

        // List must also surface the backfilled reminder
        var listed = store.List();
        Assert.Single(listed);
        Assert.Equal(reminderId, listed[0].Id);
        Assert.Equal(TrustAudience.Public, listed[0].Audience);
        Assert.Equal(SecurityPolicyDefaults.PublicBoundary, listed[0].Boundary);

        // A warning must have been logged on load (at most two — Get + List each read the file)
        Assert.NotEmpty(logger.Warnings);
        Assert.Contains(logger.Warnings, w => w.Contains(reminderId) || w.Contains("audience"));
    }

    /// <summary>
    /// Positive control: a current document with explicit Audience and Boundary round-trips
    /// correctly through a fresh store (Save then re-read).
    /// </summary>
    [Fact]
    public void Current_reminder_with_trust_fields_roundtrips_exact_values()
    {
        var store = new ReminderDefinitionStore(_paths);
        var now = TimeProvider.System.GetUtcNow();
        var id = "roundtrip-trust";

        store.Save(new ReminderDefinition
        {
            Id = id,
            Title = "Round-trip check",
            Instructions = "Do the thing.",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            Audience = TrustAudience.Personal,
            Boundary = SecurityPolicyDefaults.PersonalBoundary,
            Enabled = true,
            CreatedBy = "bob",
            CreatedAt = now,
            UpdatedAt = now
        });

        // Re-open from a fresh store instance to exercise deserialization
        var freshStore = new ReminderDefinitionStore(_paths);
        var loaded = freshStore.Get(new ReminderId(id));

        Assert.NotNull(loaded);
        Assert.Equal(TrustAudience.Personal, loaded!.Audience);
        Assert.Equal(SecurityPolicyDefaults.PersonalBoundary, loaded.Boundary);
        Assert.Equal(id, loaded.Id);
        Assert.Equal("Round-trip check", loaded.Title);
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
internal sealed class CapturingLogger<T> : ILogger<T>
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
