// -----------------------------------------------------------------------
// <copyright file="ToolApprovalStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Reads and writes persistent tool approval entries from
/// <c>~/.netclaw/config/tool-approvals.json</c>. This file is NOT monitored
/// by <see cref="ConfigWatcherService"/> — writes do not trigger daemon restart.
/// Thread-safe for concurrent reads and writes.
///
/// The on-disk schema is version 2: a typed <see cref="ApprovalEntry"/> list
/// per (audience, tool). Files lacking <c>"version": 2</c> at the root are
/// treated as legacy v1 and quarantined to <see cref="V1QuarantinePath"/>; an
/// empty v2 store is returned in their place. Files that fail to parse as
/// JSON at all are quarantined to <see cref="MalformedQuarantinePath"/>. In
/// both cases the daemon fails closed (no approvals) instead of silently
/// dropping every persisted grant.
/// </summary>
public sealed class ToolApprovalStore
{
    /// <summary>
    /// On-disk schema version emitted by <see cref="Save"/> and required by
    /// <see cref="Load"/>. Files with any other value (including absent) are
    /// quarantined to <see cref="V1QuarantinePath"/> on first read.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    private readonly string _filePath;
    private readonly object _lock = new();

    /// <summary>
    /// Path to the malformed-file quarantine sibling, used when the file
    /// cannot be parsed as JSON at all. Distinct from
    /// <see cref="V1QuarantinePath"/> so operators can tell a corrupted file
    /// apart from a legacy-version file.
    /// </summary>
    public string MalformedQuarantinePath => _filePath + ".invalid";

    /// <summary>
    /// Path to the legacy-v1 quarantine sibling, used when the file parses as
    /// JSON but does not declare schema version 2. The v1 file is preserved
    /// here untouched so operators who hand-curated v1 entries can mine them
    /// for ideas before writing fresh v2 grants.
    /// </summary>
    public string V1QuarantinePath => _filePath + ".v1.bak";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ToolApprovalStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Loads all persistent approvals from disk. Returns an empty store when
    /// the file does not exist, parses as JSON but lacks
    /// <c>"version": 2</c> (the file is moved aside to
    /// <see cref="V1QuarantinePath"/>), or fails to parse as JSON at all (the
    /// file is moved aside to <see cref="MalformedQuarantinePath"/>).
    /// </summary>
    public ToolApprovalData Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
                return new ToolApprovalData();

            var json = File.ReadAllText(_filePath);

            // Two-step parse so we can distinguish three failure modes:
            //   (1) unparseable JSON → quarantine to .invalid
            //   (2) parseable JSON but wrong schema version → quarantine to .v1.bak
            //   (3) parseable v2 JSON with deserialization error → quarantine to .invalid
            // Step 1 looks at the version field via JsonDocument; step 2 binds
            // the strongly-typed model only after the version gate passes.
            try
            {
                using var document = JsonDocument.Parse(json);
                if (!IsCurrentSchema(document.RootElement))
                {
                    QuarantineV1File();
                    return new ToolApprovalData();
                }
            }
            catch (JsonException ex)
            {
                QuarantineMalformedFile(ex);
                return new ToolApprovalData();
            }

            try
            {
                return JsonSerializer.Deserialize<ToolApprovalData>(json, JsonOptions)
                    ?? new ToolApprovalData();
            }
            catch (JsonException ex)
            {
                QuarantineMalformedFile(ex);
                return new ToolApprovalData();
            }
        }
    }

    private static bool IsCurrentSchema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("version", out var versionElem))
            return false;

        if (versionElem.ValueKind != JsonValueKind.Number)
            return false;

        return versionElem.TryGetInt32(out var version) && version == CurrentSchemaVersion;
    }

    private void QuarantineMalformedFile(JsonException cause)
    {
        try
        {
            if (File.Exists(MalformedQuarantinePath))
                File.Delete(MalformedQuarantinePath);
            File.Move(_filePath, MalformedQuarantinePath);
        }
        catch (Exception moveEx)
        {
            throw new InvalidDataException(
                $"Tool approvals file at '{_filePath}' is malformed and could not be quarantined to '{MalformedQuarantinePath}'. Inspect the file manually before restarting.",
                new AggregateException(cause, moveEx));
        }
    }

    private void QuarantineV1File()
    {
        try
        {
            if (File.Exists(V1QuarantinePath))
                File.Delete(V1QuarantinePath);
            File.Move(_filePath, V1QuarantinePath);
        }
        catch (Exception moveEx)
        {
            throw new InvalidDataException(
                $"Tool approvals file at '{_filePath}' uses a legacy schema and could not be quarantined to '{V1QuarantinePath}'. Inspect the file manually before restarting.",
                moveEx);
        }
    }

    /// <summary>
    /// Adds an approved <see cref="ApprovalEntry"/> for a tool in the given
    /// audience. The directory portion is normalized before storage so the
    /// on-disk file never accumulates trailing-slash variants of the same
    /// logical entry. Idempotent: an entry equal under
    /// <see cref="ToolApprovalEntryComparer.Equals(ApprovalEntry, ApprovalEntry)"/>
    /// is silently dropped.
    /// </summary>
    public void AddApproval(TrustAudience audience, string toolName, ApprovalEntry entry)
    {
        var normalized = ToolApprovalEntryComparer.Normalize(entry);

        lock (_lock)
        {
            var data = Load();
            var audienceKey = audience.ToWireValue();

            if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
            {
                audienceApprovals = new Dictionary<string, List<ApprovalEntry>>(StringComparer.Ordinal);
                data.Audiences[audienceKey] = audienceApprovals;
            }

            if (!audienceApprovals.TryGetValue(toolName, out var entries))
            {
                entries = [];
                audienceApprovals[toolName] = entries;
            }

            foreach (var existing in entries)
            {
                if (ToolApprovalEntryComparer.Equals(existing, normalized))
                    return;
            }

            entries.Add(normalized);
            Save(data);
        }
    }

    /// <summary>
    /// Returns the approved entries for a specific tool and audience.
    /// </summary>
    public IReadOnlyList<ApprovalEntry> GetApprovedEntries(TrustAudience audience, string toolName)
    {
        var data = Load();
        var audienceKey = audience.ToWireValue();

        if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
            return [];

        if (!audienceApprovals.TryGetValue(toolName, out var entries))
            return [];

        return entries;
    }

    /// <summary>
    /// Removes an approved entry for a tool in the given audience. Comparison
    /// uses <see cref="ToolApprovalEntryComparer.Equals(ApprovalEntry, ApprovalEntry)"/>
    /// so the CLI and the daemon agree on what "the same entry" means. Empty
    /// per-tool and per-audience maps are pruned so the file does not retain
    /// hollow sections after a revoke.
    /// </summary>
    /// <returns><c>true</c> if an entry was removed; <c>false</c> otherwise.</returns>
    public bool RemoveApproval(TrustAudience audience, string toolName, ApprovalEntry entry)
    {
        var normalized = ToolApprovalEntryComparer.Normalize(entry);

        lock (_lock)
        {
            var data = Load();
            var audienceKey = audience.ToWireValue();

            if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
                return false;

            if (!audienceApprovals.TryGetValue(toolName, out var entries))
                return false;

            var index = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                if (ToolApprovalEntryComparer.Equals(entries[i], normalized))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                return false;

            entries.RemoveAt(index);
            CleanupEmptySections(data, audienceKey, toolName);
            Save(data);
            return true;
        }
    }

    /// <summary>
    /// Removes every approval entry for a tool in the given audience.
    /// Returns the count removed; zero if the tool had no entries.
    /// </summary>
    public int RemoveAllForTool(TrustAudience audience, string toolName)
    {
        lock (_lock)
        {
            var data = Load();
            var audienceKey = audience.ToWireValue();

            if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
                return 0;

            if (!audienceApprovals.TryGetValue(toolName, out var entries))
                return 0;

            var removed = entries.Count;
            if (removed == 0)
                return 0;

            entries.Clear();
            CleanupEmptySections(data, audienceKey, toolName);
            Save(data);
            return removed;
        }
    }

    /// <summary>
    /// Returns a read-only snapshot of the current store contents, keyed by
    /// audience wire value then tool name. The snapshot is decoupled from the
    /// underlying file — subsequent mutations are not reflected.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ApprovalEntry>>> Snapshot()
    {
        var data = Load();
        var result = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ApprovalEntry>>>(StringComparer.Ordinal);
        foreach (var (audienceKey, tools) in data.Audiences)
        {
            var clonedTools = new Dictionary<string, IReadOnlyList<ApprovalEntry>>(StringComparer.Ordinal);
            foreach (var (toolName, entries) in tools)
                clonedTools[toolName] = entries.ToArray();
            result[audienceKey] = clonedTools;
        }
        return result;
    }

    private static void CleanupEmptySections(ToolApprovalData data, string audienceKey, string toolName)
    {
        if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
            return;

        if (audienceApprovals.TryGetValue(toolName, out var entries) && entries.Count == 0)
            audienceApprovals.Remove(toolName);

        if (audienceApprovals.Count == 0)
            data.Audiences.Remove(audienceKey);
    }

    private void Save(ToolApprovalData data)
    {
        // Always emit current schema version on write, even if Load returned
        // a default-constructed data object whose Version is also already 2.
        // Centralizing the write keeps the contract obvious and resilient to
        // future default-value changes on the data model.
        data.Version = CurrentSchemaVersion;

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}

/// <summary>
/// Serialization model for <c>tool-approvals.json</c>.
/// </summary>
public sealed class ToolApprovalData
{
    /// <summary>
    /// On-disk schema version. Set to <see cref="ToolApprovalStore.CurrentSchemaVersion"/>
    /// by <see cref="ToolApprovalStore.Save"/>. Files lacking this value are
    /// quarantined as legacy on first read.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = ToolApprovalStore.CurrentSchemaVersion;

    /// <summary>
    /// Per-audience approval sections. Keys are audience wire values
    /// ("personal", "team", "public"). Values are per-tool entry lists.
    /// </summary>
    [JsonPropertyName("audiences")]
    public Dictionary<string, Dictionary<string, List<ApprovalEntry>>> Audiences { get; set; } = new(StringComparer.Ordinal);
}
