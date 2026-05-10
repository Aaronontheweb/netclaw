// -----------------------------------------------------------------------
// <copyright file="AudienceTrustStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Reads and writes the two-store per-audience approval shape from
/// <c>~/.netclaw/config/tool-approvals.json</c>. Each audience contains a
/// <c>verbPatterns</c> array and a <c>trustedZones</c> array, evaluated
/// independently by the three-layer approval gate.
/// </summary>
/// <remarks>
/// Replaces the v2 <see cref="ToolApprovalStore"/> cross-product shape. The
/// new on-disk layout has audience wire values (e.g. <c>personal</c>,
/// <c>team</c>, <c>public</c>) as top-level JSON object keys with no
/// <c>version</c> or <c>audiences</c> wrapper. Files containing a top-level
/// <c>version</c> key, a top-level <c>audiences</c> key, or a top-level JSON
/// array (legacy v1) SHALL be quarantined to
/// <see cref="LegacyQuarantinePath"/> on first read; an empty new-shape store
/// SHALL be returned. No translation of legacy entries is performed.
/// </remarks>
public sealed class AudienceTrustStore
{
    private readonly string _filePath;
    private readonly object _lock = new();

    /// <summary>
    /// Sibling path used to archive any pre-existing legacy-shape file
    /// (v1 list, v2 versioned wrapper) on first read.
    /// </summary>
    public string LegacyQuarantinePath => _filePath + ".v2-discarded.bak";

    /// <summary>
    /// Sibling path used to archive a file that fails to parse as JSON.
    /// </summary>
    public string MalformedQuarantinePath => _filePath + ".invalid";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AudienceTrustStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Loads the per-audience trust state from disk. Returns an empty store
    /// when the file does not exist, contains a legacy shape (the file is
    /// moved to <see cref="LegacyQuarantinePath"/>), or fails to parse as
    /// JSON (the file is moved to <see cref="MalformedQuarantinePath"/>).
    /// </summary>
    public Dictionary<string, AudienceTrustState> Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
                return NewEmpty();

            var json = File.ReadAllText(_filePath);

            // Two-step parse so we can distinguish three failure modes:
            //   (1) unparseable JSON          → quarantine to .invalid
            //   (2) parseable JSON, legacy shape → quarantine to .v2-discarded.bak
            //   (3) parseable JSON, new shape, deserialize fails → .invalid
            // Step 1 inspects the document structure via JsonDocument; step 2
            // binds the strongly-typed model only after the legacy gate passes.
            try
            {
                using var document = JsonDocument.Parse(json);
                if (IsLegacyShape(document.RootElement))
                {
                    QuarantineLegacyFile();
                    return NewEmpty();
                }
            }
            catch (JsonException ex)
            {
                QuarantineMalformedFile(ex);
                return NewEmpty();
            }

            try
            {
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, AudienceTrustState>>(
                    json, JsonOptions);
                return deserialized ?? NewEmpty();
            }
            catch (JsonException ex)
            {
                QuarantineMalformedFile(ex);
                return NewEmpty();
            }
        }
    }

    /// <summary>
    /// Persists the supplied state to disk. Creates the parent directory if
    /// missing. Last-write-wins under the instance lock; concurrent callers
    /// across processes could race, but the daemon owns the file in practice.
    /// </summary>
    public void Save(IReadOnlyDictionary<string, AudienceTrustState> data)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
    }

    /// <summary>
    /// Adds a glob to the audience's <c>verbPatterns</c> list. Idempotent
    /// (case-insensitive duplicate detection on the trimmed pattern).
    /// Rejects patterns without a trailing arg-glob — the matcher requires
    /// the explicit <c>*</c> suffix to know where the verb chain ends.
    /// </summary>
    /// <exception cref="ArgumentException">When the pattern is empty or
    /// missing the trailing arg-glob suffix.</exception>
    public void AddVerbPattern(TrustAudience audience, string pattern)
    {
        var normalized = NormalizeVerbPattern(pattern);
        lock (_lock)
        {
            var data = Load();
            var state = GetOrCreate(data, audience);

            if (!ContainsCaseInsensitive(state.VerbPatterns, normalized))
                state.VerbPatterns.Add(normalized);

            Save(data);
        }
    }

    /// <summary>
    /// Removes a glob from the audience's <c>verbPatterns</c> list.
    /// Comparison is case-insensitive on the trimmed input. Returns
    /// <c>true</c> when an entry was removed.
    /// </summary>
    public bool RemoveVerbPattern(TrustAudience audience, string pattern)
    {
        var trimmed = (pattern ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return false;

        lock (_lock)
        {
            var data = Load();
            var audienceKey = audience.ToWireValue();

            if (!data.TryGetValue(audienceKey, out var state))
                return false;

            var index = IndexOfCaseInsensitive(state.VerbPatterns, trimmed);
            if (index < 0)
                return false;

            state.VerbPatterns.RemoveAt(index);
            CleanupEmptyAudience(data, audienceKey);
            Save(data);
            return true;
        }
    }

    /// <summary>
    /// Adds a directory glob to the audience's <c>trustedZones</c> list.
    /// Idempotent on the trimmed input (case-insensitive). Trailing slashes
    /// are normalized away so <c>/path/</c> and <c>/path</c> compare equal.
    /// </summary>
    /// <exception cref="ArgumentException">When the input is empty.</exception>
    public void AddTrustedZone(TrustAudience audience, string zoneGlob)
    {
        var normalized = NormalizeZone(zoneGlob);
        lock (_lock)
        {
            var data = Load();
            var state = GetOrCreate(data, audience);

            if (!ContainsCaseInsensitive(state.TrustedZones, normalized))
                state.TrustedZones.Add(normalized);

            Save(data);
        }
    }

    /// <summary>
    /// Removes a directory glob from the audience's <c>trustedZones</c>.
    /// Returns <c>true</c> when an entry was removed.
    /// </summary>
    public bool RemoveTrustedZone(TrustAudience audience, string zoneGlob)
    {
        var normalized = NormalizeZone(zoneGlob);
        lock (_lock)
        {
            var data = Load();
            var audienceKey = audience.ToWireValue();

            if (!data.TryGetValue(audienceKey, out var state))
                return false;

            var index = IndexOfCaseInsensitive(state.TrustedZones, normalized);
            if (index < 0)
                return false;

            state.TrustedZones.RemoveAt(index);
            CleanupEmptyAudience(data, audienceKey);
            Save(data);
            return true;
        }
    }

    /// <summary>
    /// Returns the persistent verb-pattern globs for the given audience.
    /// Empty list when the audience has no entries.
    /// </summary>
    public IReadOnlyList<string> GetVerbPatterns(TrustAudience audience)
    {
        var data = Load();
        var audienceKey = audience.ToWireValue();
        return data.TryGetValue(audienceKey, out var state)
            ? state.VerbPatterns.ToArray()
            : [];
    }

    /// <summary>
    /// Returns the persistent trusted-zone globs for the given audience.
    /// Empty list when the audience has no entries.
    /// </summary>
    public IReadOnlyList<string> GetTrustedZones(TrustAudience audience)
    {
        var data = Load();
        var audienceKey = audience.ToWireValue();
        return data.TryGetValue(audienceKey, out var state)
            ? state.TrustedZones.ToArray()
            : [];
    }

    /// <summary>
    /// Returns a read-only snapshot of the entire store. Consumers that need
    /// to enumerate every audience for display or audit should use this.
    /// </summary>
    public IReadOnlyDictionary<string, AudienceTrustState> Snapshot()
    {
        var data = Load();
        var result = new Dictionary<string, AudienceTrustState>(StringComparer.Ordinal);
        foreach (var (audienceKey, state) in data)
        {
            result[audienceKey] = new AudienceTrustState
            {
                VerbPatterns = state.VerbPatterns.ToList(),
                TrustedZones = state.TrustedZones.ToList()
            };
        }
        return result;
    }

    // -------------------------------------------------------------------
    // Schema detection — distinguishes new-shape from legacy v1/v2 files.
    // -------------------------------------------------------------------

    private static bool IsLegacyShape(JsonElement root)
    {
        // v1 stored a flat JSON array at the root.
        if (root.ValueKind == JsonValueKind.Array)
            return true;

        // Anything that isn't an object isn't valid for either schema; treat
        // as legacy/malformed and quarantine for operator inspection.
        if (root.ValueKind != JsonValueKind.Object)
            return true;

        // v2 wrapped its data under {version, audiences}; either marker is a
        // sufficient signal. Empty objects {} are valid new-shape (no audiences).
        if (root.TryGetProperty("version", out _))
            return true;

        if (root.TryGetProperty("audiences", out _))
            return true;

        return false;
    }

    private void QuarantineLegacyFile()
    {
        try
        {
            if (File.Exists(LegacyQuarantinePath))
                File.Delete(LegacyQuarantinePath);
            File.Move(_filePath, LegacyQuarantinePath);
        }
        catch (Exception moveEx)
        {
            throw new InvalidDataException(
                $"Tool approvals file at '{_filePath}' uses a legacy schema and could not be quarantined to '{LegacyQuarantinePath}'. Inspect the file manually before restarting.",
                moveEx);
        }
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

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static AudienceTrustState GetOrCreate(
        Dictionary<string, AudienceTrustState> data,
        TrustAudience audience)
    {
        var key = audience.ToWireValue();
        if (!data.TryGetValue(key, out var state))
        {
            state = new AudienceTrustState();
            data[key] = state;
        }

        return state;
    }

    private static void CleanupEmptyAudience(
        Dictionary<string, AudienceTrustState> data,
        string audienceKey)
    {
        if (!data.TryGetValue(audienceKey, out var state))
            return;

        if (state.VerbPatterns.Count == 0 && state.TrustedZones.Count == 0)
            data.Remove(audienceKey);
    }

    private static string NormalizeVerbPattern(string pattern)
    {
        var trimmed = (pattern ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Verb pattern must not be empty.", nameof(pattern));

        // The matcher uses the trailing arg-glob to know where the verb chain
        // ends. Bare verb chains (no trailing glob) have no defined match
        // semantics under the new model.
        if (!HasTrailingGlob(trimmed))
            throw new ArgumentException(
                $"Verb pattern '{trimmed}' is missing the trailing arg-glob suffix. Use '{trimmed} *' to match any args, or be more specific (e.g. 'git push origin *').",
                nameof(pattern));

        return trimmed;
    }

    private static bool HasTrailingGlob(string pattern)
    {
        // Cheap check: pattern ends with a token that contains a glob metachar.
        var lastSpace = pattern.LastIndexOf(' ');
        if (lastSpace < 0)
            return false;

        var trailing = pattern[(lastSpace + 1)..];
        return trailing.Contains('*', StringComparison.Ordinal)
            || trailing.Contains('?', StringComparison.Ordinal)
            || trailing.Contains('[', StringComparison.Ordinal);
    }

    private static string NormalizeZone(string zoneGlob)
    {
        var trimmed = (zoneGlob ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Trusted zone glob must not be empty.", nameof(zoneGlob));

        // Trailing-slash variants normalize to the no-slash form; the matcher
        // treats <dir> and <dir>/ and <dir>/* as the same zone.
        while (trimmed.Length > 1 && (trimmed[^1] == '/' || trimmed[^1] == '\\'))
            trimmed = trimmed[..^1];

        return trimmed;
    }

    private static bool ContainsCaseInsensitive(IList<string> list, string value)
        => IndexOfCaseInsensitive(list, value) >= 0;

    private static int IndexOfCaseInsensitive(IList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static Dictionary<string, AudienceTrustState> NewEmpty()
        => new(StringComparer.Ordinal);
}
