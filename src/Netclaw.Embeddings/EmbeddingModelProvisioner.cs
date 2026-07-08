// -----------------------------------------------------------------------
// <copyright file="EmbeddingModelProvisioner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;

namespace Netclaw.Embeddings;

/// <summary>
/// One entry in <see cref="EmbeddingModelProvisioner.Allowlist"/>: everything needed to fetch
/// and verify one embedding model's artifacts. <see cref="ModelUrl"/>/<see cref="TokenizerUrl"/>
/// are pinned to a specific upstream commit (not a mutable branch) so the pinned SHA-256 values
/// can never silently stop matching what the URL serves.
/// </summary>
/// <param name="ModelId">Allowlist key, e.g. <c>snowflake-arctic-embed-m</c>.</param>
/// <param name="ModelUrl">Download location for <c>model.onnx</c>.</param>
/// <param name="TokenizerUrl">Download location for the WordPiece <c>vocab.txt</c>.</param>
/// <param name="ModelSha256">Expected SHA-256 (lowercase hex) of the model artifact.</param>
/// <param name="TokenizerSha256">Expected SHA-256 (lowercase hex) of the vocab artifact.</param>
/// <param name="Dimensions">Embedding vector width this model produces.</param>
/// <param name="ModelByteSize">Expected byte size of the model artifact — a cheap first check before hashing.</param>
public sealed record EmbeddingModelManifestEntry(
    string ModelId,
    Uri ModelUrl,
    Uri TokenizerUrl,
    string ModelSha256,
    string TokenizerSha256,
    int Dimensions,
    long ModelByteSize);

/// <summary>Files placed on disk by <see cref="EmbeddingModelProvisioner.ProvisionAsync"/>, ready for <see cref="OnnxMemoryEmbedder.LoadAsync"/>.</summary>
public sealed record ProvisionedEmbeddingModel(string ModelId, string ModelPath, string VocabPath, int Dimensions);

/// <summary>
/// One entry in <see cref="EmbeddingModelProvisioner.RelevanceAllowlist"/> (memory-relevance-gate
/// D3): the same download/verification fields as <see cref="EmbeddingModelManifestEntry"/>, minus
/// <c>Dimensions</c> (a cross-encoder produces a single logit, not a fixed-width vector) and plus
/// <see cref="CalibratedThreshold"/> — the one field embedding manifests don't need. The
/// threshold travels with the model id it was measured against so a future model swap can never
/// silently reuse a threshold calibrated for a different model's score distribution.
/// </summary>
/// <param name="ModelId">Allowlist key, e.g. <c>ms-marco-minilm-l-6-v2</c>.</param>
/// <param name="ModelUrl">Download location for <c>model.onnx</c>.</param>
/// <param name="TokenizerUrl">Download location for the WordPiece <c>vocab.txt</c>.</param>
/// <param name="ModelSha256">Expected SHA-256 (lowercase hex) of the model artifact.</param>
/// <param name="TokenizerSha256">Expected SHA-256 (lowercase hex) of the vocab artifact.</param>
/// <param name="ModelByteSize">Expected byte size of the model artifact — a cheap first check before hashing.</param>
/// <param name="CalibratedThreshold">
/// The similarity threshold calibrated for this model id's score distribution (memory-relevance-gate
/// D2: S*=0.02 for the shipped <c>ms-marco-minilm-l-6-v2</c>). Governs gating unless the operator
/// configures an explicit <c>Memory.Recall.RelevanceGate.Threshold</c> override.
/// </param>
public sealed record RelevanceModelManifestEntry(
    string ModelId,
    Uri ModelUrl,
    Uri TokenizerUrl,
    string ModelSha256,
    string TokenizerSha256,
    long ModelByteSize,
    double CalibratedThreshold);

/// <summary>Files placed on disk by <see cref="EmbeddingModelProvisioner.ProvisionRelevanceModelAsync"/>, ready for <c>OnnxCrossEncoderScorer.LoadAsync</c>.</summary>
public sealed record ProvisionedRelevanceModel(string ModelId, string ModelPath, string VocabPath, double CalibratedThreshold);

/// <summary>
/// Thrown when a requested model id is not on the allowlist, or a downloaded artifact fails
/// byte-size or SHA-256 verification. Never wraps a partially-written file — callers can treat
/// this as "nothing was provisioned."
/// </summary>
public sealed class EmbeddingModelProvisioningException(string message) : Exception(message);

/// <summary>
/// Downloads and verifies embedding model artifacts against a pinned in-code allowlist
/// (memory-core-redesign D2) — a supply-chain boundary. Arbitrary model URLs are rejected by
/// construction: there is no code path that accepts a caller-supplied URL, only a caller-
/// supplied <see cref="EmbeddingModelManifestEntry.ModelId"/> looked up in
/// <see cref="Allowlist"/>. This type performs no daemon wiring, no <see cref="OnnxMemoryEmbedder"/>
/// construction, and no warm-up inference — it only gets verified files onto disk.
/// </summary>
public sealed class EmbeddingModelProvisioner
{
    /// <summary>
    /// Pinned allowlist: model id → download locations, expected hashes, and dimensions.
    /// Primary is <c>snowflake-arctic-embed-m</c> (May-2026-ratified nominator model);
    /// <c>mxbai-embed-large-v1</c> is the allowlisted fallback. Both entries point at the
    /// plain fp32 <c>onnx/model.onnx</c> artifact (not the int8/fp16/quantized variants also
    /// published on HuggingFace) for correctness; a quantized variant is a future optimization,
    /// not this stage's concern. URLs are pinned to a specific HuggingFace repo commit sha
    /// (not <c>main</c>) so the pinned hash can never silently drift out of sync with what the
    /// URL serves.
    /// </summary>
    public static IReadOnlyDictionary<string, EmbeddingModelManifestEntry> Allowlist { get; } =
        new Dictionary<string, EmbeddingModelManifestEntry>(StringComparer.Ordinal)
        {
            ["snowflake-arctic-embed-m"] = new EmbeddingModelManifestEntry(
                ModelId: "snowflake-arctic-embed-m",
                ModelUrl: new Uri("https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/fc74610d18462d218e312aa986ec5c8a75a98152/onnx/model.onnx"),
                TokenizerUrl: new Uri("https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/fc74610d18462d218e312aa986ec5c8a75a98152/vocab.txt"),
                ModelSha256: "564e6c65ee0c739a486702e9e3e9b33c3f697c19c34dbe886bce9eec497ce971",
                TokenizerSha256: "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                Dimensions: 768,
                ModelByteSize: 435_811_541),

            ["mxbai-embed-large-v1"] = new EmbeddingModelManifestEntry(
                ModelId: "mxbai-embed-large-v1",
                ModelUrl: new Uri("https://huggingface.co/mixedbread-ai/mxbai-embed-large-v1/resolve/b33106f585b9ce46904ad7443a3b52b7a63e231c/onnx/model.onnx"),
                TokenizerUrl: new Uri("https://huggingface.co/mixedbread-ai/mxbai-embed-large-v1/resolve/b33106f585b9ce46904ad7443a3b52b7a63e231c/vocab.txt"),
                ModelSha256: "adb53ed475faa339bfad3bd2bdb7e6a30b4f47280ade9811f81bef7953f9ab77",
                TokenizerSha256: "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                Dimensions: 1024,
                ModelByteSize: 1_336_854_282),
        };

    /// <summary>
    /// Allowlist key for the single ratified relevance (cross-encoder) model
    /// (memory-relevance-gate D2). Unlike embeddings, there is no operator-facing model-choice
    /// knob for the relevance gate — the shoot-out ratified exactly one design/model pair, so
    /// this id is a fixed constant rather than a <c>Memory.Recall.RelevanceGate</c> config
    /// property.
    /// </summary>
    public const string DefaultRelevanceModelId = "ms-marco-minilm-l-6-v2";

    /// <summary>
    /// Pinned allowlist for relevance (cross-encoder) models — the same supply-chain mechanism
    /// as <see cref="Allowlist"/>, generalized to a manifest entry kind that additionally carries
    /// a calibrated operating threshold (memory-relevance-gate D2/D3). <c>Xenova/ms-marco-MiniLM-L-6-v2</c>
    /// is the winner of a 4-design measured shoot-out, re-validated out-of-sample (see
    /// <c>openspec/changes/memory-relevance-gate/design.md</c> D2): quantized int8,
    /// bit-for-bit quality-identical to the fp32 variant on both gold sets at a fraction of the
    /// RAM. URL is pinned to the repo's HEAD commit sha at the time this artifact was verified
    /// (not <c>main</c>), matching <see cref="Allowlist"/>'s own pinning convention.
    /// </summary>
    public static IReadOnlyDictionary<string, RelevanceModelManifestEntry> RelevanceAllowlist { get; } =
        new Dictionary<string, RelevanceModelManifestEntry>(StringComparer.Ordinal)
        {
            [DefaultRelevanceModelId] = new RelevanceModelManifestEntry(
                ModelId: DefaultRelevanceModelId,
                ModelUrl: new Uri("https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/a09144355adeed5f58c8ed011d209bf8ee5a1fec/onnx/model_quantized.onnx"),
                TokenizerUrl: new Uri("https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/a09144355adeed5f58c8ed011d209bf8ee5a1fec/vocab.txt"),
                ModelSha256: "e9d8ebf845c413e981c175bfe49a3bfa9b3dcce2a3ba54875ee5df5a58639fbe",
                TokenizerSha256: "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                ModelByteSize: 23_143_499,
                CalibratedThreshold: 0.02),
        };

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyDictionary<string, EmbeddingModelManifestEntry> _allowlist;

    /// <param name="httpClient">Used for all artifact downloads.</param>
    /// <param name="allowlist">
    /// The allowlist to resolve model ids against — an explicit, required dependency rather
    /// than always reading the static <see cref="Allowlist"/> internally, so tests can supply
    /// a small allowlist pointed at a local HTTP fixture instead of ever reaching the real
    /// HuggingFace URLs. Production wiring passes <see cref="Allowlist"/> itself.
    /// </param>
    public EmbeddingModelProvisioner(HttpClient httpClient, IReadOnlyDictionary<string, EmbeddingModelManifestEntry> allowlist)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(allowlist);
        _httpClient = httpClient;
        _allowlist = allowlist;
    }

    /// <summary>
    /// Downloads and verifies <paramref name="modelId"/>'s artifacts into
    /// <paramref name="destinationDirectory"/> as <c>model.onnx</c> and <c>vocab.txt</c>. Each
    /// download lands in a temp file first and is only renamed into place (atomic on the same
    /// filesystem) after its SHA-256 (and, for the model file, byte size) matches the allowlist
    /// entry — a hash mismatch discards the temp file and throws
    /// <see cref="EmbeddingModelProvisioningException"/> without ever creating or replacing the
    /// destination file.
    ///
    /// <para>
    /// When both destination files already exist and hash-verify against the allowlist entry,
    /// this method returns immediately without any network access (memory-core-redesign task
    /// 2.7: "already-provisioned+hash-valid loads without network"). This makes repeated calls
    /// — e.g. the daemon's warmup service running on every restart — idempotent and safe to run
    /// with <c>AutoDownload=false</c> once a model has been provisioned at least once.
    /// </para>
    /// </summary>
    public async Task<ProvisionedEmbeddingModel> ProvisionAsync(
        string modelId,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        if (!_allowlist.TryGetValue(modelId, out var entry))
        {
            throw new EmbeddingModelProvisioningException(
                $"Unknown embedding model id '{modelId}'. Allowlisted ids: {string.Join(", ", _allowlist.Keys.Order(StringComparer.Ordinal))}.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var modelPath = Path.Combine(destinationDirectory, "model.onnx");
        var vocabPath = Path.Combine(destinationDirectory, "vocab.txt");

        if (await IsValidAsync(modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false)
            && await IsValidAsync(vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false))
        {
            return new ProvisionedEmbeddingModel(modelId, modelPath, vocabPath, entry.Dimensions);
        }

        await DownloadAndVerifyAsync(entry.ModelUrl, modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false);
        await DownloadAndVerifyAsync(entry.TokenizerUrl, vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false);

        return new ProvisionedEmbeddingModel(modelId, modelPath, vocabPath, entry.Dimensions);
    }

    /// <summary>
    /// Verifies whether <paramref name="modelId"/>'s artifacts are already present and
    /// hash-valid at <paramref name="destinationDirectory"/>, without ever accessing the
    /// network. Returns null when the model id is unknown to the allowlist, or either file is
    /// missing or fails verification (including a corrupted local copy) — callers that must
    /// never trigger a download use this instead of <see cref="ProvisionAsync"/>
    /// (memory-core-redesign task 2.7: <c>Memory.Embeddings.AutoDownload=false</c> gates the
    /// network path entirely, even to repair a bad local copy).
    /// </summary>
    public async Task<ProvisionedEmbeddingModel?> TryLoadVerifiedAsync(
        string modelId,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        if (!_allowlist.TryGetValue(modelId, out var entry))
            return null;

        var modelPath = Path.Combine(destinationDirectory, "model.onnx");
        var vocabPath = Path.Combine(destinationDirectory, "vocab.txt");

        if (!await IsValidAsync(modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false))
            return null;
        if (!await IsValidAsync(vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false))
            return null;

        return new ProvisionedEmbeddingModel(modelId, modelPath, vocabPath, entry.Dimensions);
    }

    /// <summary>
    /// Downloads and verifies <paramref name="modelId"/>'s relevance-model artifacts (memory-
    /// relevance-gate D3) — identical download/atomic-rename/hash-verify code path as
    /// <see cref="ProvisionAsync"/>, reused unchanged; only the manifest entry type differs.
    /// The allowlist is a method parameter rather than a constructor-injected field (unlike
    /// <see cref="_allowlist"/>) so this and <see cref="TryLoadVerifiedRelevanceModelAsync"/> can
    /// be added without perturbing every existing embedding-only call site's constructor call —
    /// callers pass <see cref="RelevanceAllowlist"/> in production, or a small fixture-pointed
    /// dictionary in tests.
    /// </summary>
    public async Task<ProvisionedRelevanceModel> ProvisionRelevanceModelAsync(
        string modelId,
        IReadOnlyDictionary<string, RelevanceModelManifestEntry> allowlist,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        if (!allowlist.TryGetValue(modelId, out var entry))
        {
            throw new EmbeddingModelProvisioningException(
                $"Unknown relevance model id '{modelId}'. Allowlisted ids: {string.Join(", ", allowlist.Keys.Order(StringComparer.Ordinal))}.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var modelPath = Path.Combine(destinationDirectory, "model.onnx");
        var vocabPath = Path.Combine(destinationDirectory, "vocab.txt");

        if (await IsValidAsync(modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false)
            && await IsValidAsync(vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false))
        {
            return new ProvisionedRelevanceModel(modelId, modelPath, vocabPath, entry.CalibratedThreshold);
        }

        await DownloadAndVerifyAsync(entry.ModelUrl, modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false);
        await DownloadAndVerifyAsync(entry.TokenizerUrl, vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false);

        return new ProvisionedRelevanceModel(modelId, modelPath, vocabPath, entry.CalibratedThreshold);
    }

    /// <summary>
    /// Verifies whether <paramref name="modelId"/>'s relevance-model artifacts are already
    /// present and hash-valid at <paramref name="destinationDirectory"/>, without ever accessing
    /// the network — the relevance-model analogue of <see cref="TryLoadVerifiedAsync"/>, used
    /// when <c>Memory.Embeddings.AutoDownload=false</c> gates the network path entirely.
    /// </summary>
    public async Task<ProvisionedRelevanceModel?> TryLoadVerifiedRelevanceModelAsync(
        string modelId,
        IReadOnlyDictionary<string, RelevanceModelManifestEntry> allowlist,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        if (!allowlist.TryGetValue(modelId, out var entry))
            return null;

        var modelPath = Path.Combine(destinationDirectory, "model.onnx");
        var vocabPath = Path.Combine(destinationDirectory, "vocab.txt");

        if (!await IsValidAsync(modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false))
            return null;
        if (!await IsValidAsync(vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false))
            return null;

        return new ProvisionedRelevanceModel(modelId, modelPath, vocabPath, entry.CalibratedThreshold);
    }

    private static async Task<bool> IsValidAsync(string path, string expectedSha256, long? expectedByteSize, CancellationToken ct)
    {
        if (!File.Exists(path))
            return false;

        if (expectedByteSize is { } expected && new FileInfo(path).Length != expected)
            return false;

        var actualSha256 = await ComputeSha256Async(path, ct).ConfigureAwait(false);
        return string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadAndVerifyAsync(
        Uri source,
        string destinationPath,
        string expectedSha256,
        long? expectedByteSize,
        CancellationToken ct)
    {
        var tempPath = $"{destinationPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var responseStream = await _httpClient.GetStreamAsync(source, ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await responseStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            // Cheap fail-fast before hashing a potentially large file: a truncated or swapped
            // artifact almost always has the wrong size.
            var actualByteSize = new FileInfo(tempPath).Length;
            if (expectedByteSize is { } expected && actualByteSize != expected)
            {
                throw new EmbeddingModelProvisioningException(
                    $"Downloaded artifact from {source} is {actualByteSize} bytes; the allowlist for this entry expects {expected} bytes. " +
                    "Discarding — this is a supply-chain integrity boundary, never loaded.");
            }

            var actualSha256 = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddingModelProvisioningException(
                    $"Downloaded artifact from {source} does not match the pinned SHA-256 (expected {expectedSha256}, got {actualSha256}). " +
                    "Discarding — this is a supply-chain integrity boundary, never loaded.");
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            // No-op once Move above has succeeded (the file no longer exists at tempPath);
            // cleans up the partial download on any failure path, including a hash/size
            // mismatch or a cancelled/faulted copy.
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
