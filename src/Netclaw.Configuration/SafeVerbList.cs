// -----------------------------------------------------------------------
// <copyright file="SafeVerbList.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Curated list of demonstrably read-only shell verb chains the approval gate
/// auto-allows when invoked inside an audience-aware safe space. Loaded from
/// the daemon's bundled <c>safe-verbs.&lt;os&gt;.json</c> resource, optionally
/// merged additively with a user override at
/// <c>~/.netclaw/config/safe-verbs.&lt;os&gt;.json</c>.
///
/// Membership is exact-equality against the verb chain extracted by
/// <c>ShellTokenizer.ExtractVerbChain</c> (case rules from
/// <see cref="ToolApprovalEntryComparer.Comparer"/>: Ordinal on POSIX,
/// OrdinalIgnoreCase on Windows). Mutating verbs (e.g. <c>git push</c>,
/// <c>sed -i</c>) are intentionally absent — they remain subject to the
/// interactive approval gate.
/// </summary>
public sealed class SafeVerbList
{
    public static readonly SafeVerbList Empty = new(new HashSet<string>(StringComparer.Ordinal));

    private readonly HashSet<string> _verbs;

    internal SafeVerbList(HashSet<string> verbs)
    {
        _verbs = verbs;
    }

    /// <summary>
    /// Builds a <see cref="SafeVerbList"/> from an explicit verb collection.
    /// Used by tests and by callers that synthesize a list outside the
    /// bundled-plus-override loading path.
    /// </summary>
    public static SafeVerbList FromVerbs(IEnumerable<string> verbs)
    {
        var set = new HashSet<string>(ToolApprovalEntryComparer.Comparer);
        foreach (var verb in verbs)
        {
            if (!string.IsNullOrWhiteSpace(verb))
                set.Add(verb.Trim());
        }
        return new SafeVerbList(set);
    }

    /// <summary>
    /// Returns true when the candidate verb chain is on the safe-verbs list.
    /// </summary>
    public bool Contains(string candidateVerb)
        => !string.IsNullOrEmpty(candidateVerb) && _verbs.Contains(candidateVerb);

    /// <summary>The verbs in this list. Stable ordering; intended for diagnostics, not lookups.</summary>
    public IReadOnlyCollection<string> Verbs => _verbs;
}

/// <summary>
/// JSON deserialization shape for <c>safe-verbs.*.json</c> files.
/// </summary>
internal sealed class SafeVerbListFile
{
    [JsonPropertyName("verbs")]
    public List<string> Verbs { get; set; } = new();
}

/// <summary>
/// Loads the bundled safe-verbs list for the current OS and merges any user
/// override at <see cref="NetclawPaths.SafeVerbsOverridePath"/> additively.
/// </summary>
public static class SafeVerbLoader
{
    private const string LinuxResourceName = "Netclaw.Configuration.SafeVerbs.safe-verbs.linux.json";
    private const string WindowsResourceName = "Netclaw.Configuration.SafeVerbs.safe-verbs.windows.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Loads the safe-verbs list for the current OS. Always returns at least
    /// the bundled defaults; merges an additional set from
    /// <see cref="NetclawPaths.SafeVerbsOverridePath"/> when that file exists
    /// and parses cleanly. Never throws — a malformed override file is
    /// silently ignored (the bundled defaults still apply).
    /// </summary>
    public static SafeVerbList Load(NetclawPaths? paths = null)
        => Load(OperatingSystem.IsWindows(), paths?.SafeVerbsOverridePath);

    internal static SafeVerbList Load(bool isWindows, string? overrideFilePath)
    {
        var comparer = ToolApprovalEntryComparer.Comparer;
        var verbs = new HashSet<string>(comparer);

        foreach (var verb in LoadBundled(isWindows))
            verbs.Add(verb);

        if (!string.IsNullOrWhiteSpace(overrideFilePath) && File.Exists(overrideFilePath))
        {
            foreach (var verb in TryLoadOverride(overrideFilePath))
                verbs.Add(verb);
        }

        return new SafeVerbList(verbs);
    }

    private static IEnumerable<string> LoadBundled(bool isWindows)
    {
        var resourceName = isWindows ? WindowsResourceName : LinuxResourceName;
        var assembly = typeof(SafeVerbLoader).Assembly;

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Bundled safe-verbs resource '{resourceName}' is missing from {assembly.FullName}. "
                + "This is a build packaging error: SafeVerbs/*.json must be embedded.");

        var file = JsonSerializer.Deserialize<SafeVerbListFile>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Bundled safe-verbs resource '{resourceName}' deserialized to null.");

        foreach (var verb in file.Verbs)
        {
            if (!string.IsNullOrWhiteSpace(verb))
                yield return verb.Trim();
        }
    }

    private static IEnumerable<string> TryLoadOverride(string path)
    {
        SafeVerbListFile? file;
        try
        {
            var json = File.ReadAllText(path);
            file = JsonSerializer.Deserialize<SafeVerbListFile>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A malformed user override should not prevent the daemon from
            // starting; the bundled defaults remain in force. The doctor
            // surfaces this condition out-of-band so operators can fix it
            // without losing trust in the safe-verb policy.
            yield break;
        }

        if (file?.Verbs is null)
            yield break;

        foreach (var verb in file.Verbs)
        {
            if (!string.IsNullOrWhiteSpace(verb))
                yield return verb.Trim();
        }
    }
}
