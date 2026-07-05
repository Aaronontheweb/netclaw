// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

// Honest one-shot bench harness for OnnxMemoryEmbedder (memory-core-redesign task 2.13, extended
// for Slice 4 Stage A). Three modes, dispatched from the first CLI argument:
//
//   latency <modelId> [modelDir]   Percentile latency table (short/medium/doc/concurrency/cold
//                                  load) for one allowlisted model, loaded exactly the way the
//                                  daemon would (provisioner hash-verify, then
//                                  OnnxMemoryEmbedder.LoadAsync — dynamic bucket-of-8 sequence
//                                  length is unconditional production behavior now, not a
//                                  bench-only path). Default modelId is the shipped default
//                                  (EmbeddingModelProvisioner default via MemoryEmbeddingsConfig).
//
//   parity [fp32Dir] [int8Dir]     Quality-parity gate: embeds a fixed probe set (20 short
//                                  queries + 20 doc-like paragraphs + 10 near-duplicate pairs +
//                                  5 unrelated pairs) under both the fp32 and int8 allowlisted
//                                  models and reports per-sentence cosine parity plus
//                                  ranking-preservation (pair-cosine delta) between them. This
//                                  run loads two models in one process — its own RSS numbers are
//                                  not the reported "int8 RSS" figure (see `latency` for that);
//                                  it only proves quality, deliberately kept separate from the
//                                  RSS measurement so the two don't contaminate each other.
//
//   arena <modelId> [modelDir] [arenaOn|arenaOff] [patternOn|patternOff]
//                                  Mixed-workload (doc burst, then a long steady run of short
//                                  queries) RSS/latency measurement for one SessionOptions
//                                  arena/memory-pattern combination, so the four combinations can
//                                  be compared across four separate process runs (clean VmHWM per
//                                  run). This is throwaway investigation for Slice 4 Stage A's
//                                  arena-tuning decision — whichever combination wins gets
//                                  hardcoded into OnnxMemoryEmbedder.LoadAsync with a comment, not
//                                  exposed as a new knob.
//
// Never downloads anything: if a model directory is missing or fails SHA-256 verification
// against EmbeddingModelProvisioner.Allowlist, this exits with an error instead of fetching it.
//
// Usage: dotnet run -c Release --project tools/embed-latency-bench -- <mode> [args...]

using System.Diagnostics;
using System.Numerics.Tensors;
using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Netclaw.Embeddings;

const int MaxTokens = 512;
const int SequenceLengthBucket = 8;
const int WarmupIterations = 20;
const int TimedIterations = 200;
const int ConcurrencyIterationsPerLoop = 100;

string ModelsRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "recall-research-local", "models");
string DefaultDirFor(string modelId) => Path.Combine(ModelsRoot(), modelId);

// /proc-based memory sampling: VmRSS is the resident set size *right now*; VmHWM ("high water
// mark") is the peak RSS the kernel has ever observed for this process, tracked continuously
// regardless of when we happen to sample it — this is what makes an accurate "peak RSS" number
// possible without a concurrent external polling loop.
long ReadProcStatusKb(string field)
{
    foreach (var line in File.ReadLines("/proc/self/status"))
    {
        if (!line.StartsWith(field, StringComparison.Ordinal))
            continue;
        var digits = new string(line.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? -1 : long.Parse(digits);
    }
    return -1;
}

long RssMb() => ReadProcStatusKb("VmRSS:") / 1024;
long PeakRssMb() => ReadProcStatusKb("VmHWM:") / 1024;

void ReportRss(string label) => Console.WriteLine($"  RSS [{label}]: {RssMb()} MB (VmHWM so far: {PeakRssMb()} MB)");

async Task<OnnxMemoryEmbedder> LoadVerifiedAsync(string modelId, string modelDir)
{
    using var httpClient = new HttpClient(); // required by the provisioner's constructor; TryLoadVerifiedAsync never touches the network.
    var provisioner = new EmbeddingModelProvisioner(httpClient, EmbeddingModelProvisioner.Allowlist);
    var verified = await provisioner.TryLoadVerifiedAsync(modelId, modelDir);
    if (verified is null)
        throw new InvalidOperationException(
            $"STOP: '{modelDir}' does not contain a hash-verified '{modelId}' (model.onnx + vocab.txt) " +
            "matching EmbeddingModelProvisioner.Allowlist. Refusing to proceed — this tool never downloads.");
    Console.WriteLine($"Verified model: {verified.ModelId} ({verified.Dimensions} dims) at {verified.ModelPath}");
    return await OnnxMemoryEmbedder.LoadAsync(verified.ModelPath, verified.VocabPath, verified.ModelId, verified.Dimensions);
}

// --- Shared corpora (deterministic, hardcoded) -----------------------------------------------

string[] shortQueries =
[
    "what's our grafana dashboard convention?",
    "how do I restart the daemon safely?",
    "where do we store the slack webhook secret?",
    "what does MinCosineSimilarity default to in production?",
    "did we ever decide on mirroring model artifacts into R2?",
    "what version is pinned in Directory.Build.props right now?",
    "how many logical cores does the reference box have?",
    "which model is the allowlist's default embedder?",
    "what's the checkpoint worker's idle loop actually for?",
    "can you summarize yesterday's release notes for me?",
    "who owns the memory_embeddings table schema change?",
    "what's the config key for the recall timeout?",
    "is the semaphore capped at two concurrent inference calls?",
    "what tokenizer library are we using for BERT models?",
    "when did we last run the full eval suite?",
    "what's the vector weight in the hybrid fusion score?",
    "how do I run the light smoke test suite locally?",
    "what's currently blocking slice four from shipping?",
    "which subreddit rule blocks self-promotional posts?",
    "what does netclaw doctor --fix actually repair?",
];

// Medium/doc-length corpora are built from a fixed sentence bank (thematically real content
// about this codebase) rather than hand-authored essays, so their length is deterministically
// controllable; actual token counts are measured below rather than assumed.
string[] sentenceBank =
[
    "The daemon persists session state under the Slack thread identity of channelId and threadTs, so every conversation maps to exactly one actor.",
    "Query embedding runs in-process through OnnxRuntime with CLS-token pooling and L2 normalization before the vector is compared against stored memories.",
    "The recall coordinator merges FTS5 lexical candidates with vector nearest-neighbor candidates before applying the policy gates uniformly across both sources.",
    "Consolidation only executes from a human-ratified plan file, never automatically, and always takes a VACUUM INTO backup before touching the live database.",
    "The expiry sweep runs inside the checkpoint worker's idle loop and deletes rows whose expires_at timestamp has already passed the grace window.",
    "MinCosineSimilarity acts as an absolute floor rather than a relative rank cutoff, so a mediocre top candidate can still be suppressed entirely.",
    "The embedding model allowlist pins a specific HuggingFace commit SHA for both the model weights and the tokenizer vocabulary file.",
    "SchemaFixResolver can only repair validation errors it recognizes, so new enum properties must ship as strings with named values from day one.",
    "Akka.Hosting wires the actor system through dependency injection, keeping the constructor signature explicit about every collaborator the actor needs.",
    "The bounded concurrency gate caps simultaneous ONNX inference calls at two by default, sharing the CPU predictably with the rest of the daemon.",
    "TimeProvider is injected everywhere instead of DateTimeOffset.UtcNow so that tests can advance a virtual clock without any wall-clock sleeping.",
    "The nominator model and the fallback model both export fp32 ONNX graphs with add_pooling_layer disabled, so pooling always happens in application code.",
    "Backfill re-embeds only rows whose content hash no longer matches the stored hash, making repeated runs of the same backfill essentially free.",
    "The doctor command surfaces embedding coverage gaps, model hash mismatches, and mixed-model rows as loud warnings rather than silent degradation.",
    "Slopwatch flags disabled tests, suppressed warnings, and empty catch blocks as reward-hacking signals that must be fixed or explicitly baselined.",
    "The observer sidecar proposes a recall mode for each distilled memory, and the policy gate honors that proposal for durable facts by default.",
    "A crash between the document commit and the embedding upsert leaves a coverage gap that the next backfill pass repairs automatically.",
    "The vector index is a flat in-memory array per model, invalidated by a store version counter whenever the underlying table changes.",
    "Structural append is the fallback path whenever the merge guard rejects a synthesized body for losing too many load-bearing tokens.",
    "Trace-class memories are short-lived operational state with a seventy-two hour time-to-live, weighted below durable facts during recall scoring.",
    "The tool-lessons block is injected once per tool per session as an exact anchor-id lookup, entirely outside the pre-turn recall budget.",
    "Recency decay multiplies the fused score by a floor-bounded factor derived from a configurable half-life measured in days.",
    "Every configuration schema uses additionalProperties false, so an unlisted property on any Config type is rejected at doctor time.",
    "The release version gate checks that the pushed tag matches VersionPrefix and VersionSuffix exactly, rejecting any other tag shape.",
    "Prerelease tags always use the dotted beta.N form, because a mixed identifier like beta1 sorts lexically in the wrong order.",
    "The memory store's InitializeAsync method creates the embeddings table idempotently, independent of the daemon's own migration pipeline.",
    "Evidence records are policy-forced into an immutable, searchable class, which is why lessons needed their own dedicated memory class instead.",
    "The 22 legacy compaction rows were repaired directly during the quick-win slice, ahead of the taxonomy rebalance that formalized the invariant.",
    "Content hash is computed over the normalized title and body concatenation, using SHA-256 the same way the provisioner verifies model artifacts.",
    "A rate-limited log line fires whenever vector recall degrades to lexical-only, so operators see the condition without being flooded by it.",
];

string BuildFromBank(int startIndex, int count)
{
    var parts = new string[count];
    for (var i = 0; i < count; i++)
        parts[i] = sentenceBank[(startIndex + i) % sentenceBank.Length];
    return string.Join(' ', parts);
}

string[] mediumCorpus = Enumerable.Range(0, 20)
    .Select(i => BuildFromBank(startIndex: i * 3, count: 6))
    .ToArray();

string[] docCorpus = Enumerable.Range(0, 20)
    .Select(i => BuildFromBank(startIndex: i * 7, count: 15))
    .ToArray();

// --- Parity-mode probe set (task A3): 10 near-duplicate pairs + 5 clearly-unrelated pairs. The
// 20-short + 20-doc-like "per-sentence" half of the probe set reuses shortQueries/docCorpus above
// verbatim rather than inventing a second corpus with the same shape.
(string A, string B)[] nearDupPairs =
[
    ("MinCosineSimilarity acts as an absolute floor rather than a relative rank cutoff.",
     "The absolute-floor semantics of MinCosineSimilarity mean it isn't just a relative ranking cutoff."),
    ("The daemon persists session state under the Slack thread identity of channelId and threadTs.",
     "Every conversation's session state is keyed by the pair of Slack channel id and thread timestamp."),
    ("The bounded concurrency gate caps simultaneous ONNX inference calls at two by default.",
     "By default, no more than two ONNX inference calls run concurrently thanks to the bounded concurrency gate."),
    ("Consolidation only executes from a human-ratified plan file, never automatically.",
     "A human must ratify a plan file before consolidation ever runs; it never fires on its own."),
    ("The embedding model allowlist pins a specific HuggingFace commit SHA for the model weights.",
     "Model weights are downloaded from a HuggingFace URL that is pinned to one specific commit SHA."),
    ("Trace-class memories are short-lived operational state with a seventy-two hour time-to-live.",
     "Trace memories only live for seventy-two hours because they capture short-lived operational state."),
    ("The recall coordinator merges FTS5 lexical candidates with vector nearest-neighbor candidates.",
     "Lexical hits from FTS5 and vector nearest-neighbor hits are merged together by the recall coordinator."),
    ("SchemaFixResolver can only repair validation errors it explicitly recognizes.",
     "Only the validation errors that SchemaFixResolver already knows about can be automatically repaired."),
    ("A crash between the document commit and the embedding upsert leaves a coverage gap.",
     "If the process crashes after the document commits but before the embedding upsert, a coverage gap results."),
    ("Prerelease tags always use the dotted beta.N form because a mixed identifier sorts wrong.",
     "The dotted beta.N convention for prerelease tags exists specifically because beta1-style identifiers sort incorrectly."),
];

(string A, string B)[] unrelatedPairs =
[
    ("The daemon persists session state under the Slack thread identity of channelId and threadTs.",
     "Bake the bread at four hundred fifty degrees for twenty five minutes until the crust turns golden brown."),
    ("MinCosineSimilarity acts as an absolute floor rather than a relative rank cutoff.",
     "The hiking trail climbs two thousand feet over six miles before reaching the granite summit ridge."),
    ("The bounded concurrency gate caps simultaneous ONNX inference calls at two by default.",
     "Migratory geese fly in a V formation to reduce drag and conserve energy over long distances."),
    ("Consolidation only executes from a human-ratified plan file, never automatically.",
     "The violin section tuned to the oboe's A440 reference pitch before the orchestra began rehearsing."),
    ("The embedding model allowlist pins a specific HuggingFace commit SHA for the model weights.",
     "Fresh basil, garlic, pine nuts, and olive oil are blended together to make a simple pesto sauce."),
];

// --- Shared percentile helpers -----------------------------------------------------------------

Row Percentiles(string label, List<double> samplesMs)
{
    var sorted = samplesMs.Order().ToArray();
    double Pct(double p)
    {
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    return new Row(label, sorted.Length, Pct(50), Pct(90), Pct(95), Pct(99), sorted[^1], sorted.Average());
}

void PrintRows(IEnumerable<Row> rows)
{
    Console.WriteLine($"{"corpus",-28}{"n",5}{"p50",8}{"p90",8}{"p95",8}{"p99",8}{"max",8}{"mean",8}   (ms, batch=1)");
    foreach (var row in rows)
        Console.WriteLine($"{row.Label,-28}{row.N,5}{row.P50,8:F1}{row.P90,8:F1}{row.P95,8:F1}{row.P99,8:F1}{row.Max,8:F1}{row.Mean,8:F1}");
}

async Task<List<double>> RunCorpus(OnnxMemoryEmbedder embedder, string[] corpus, int warmup, int timed)
{
    for (var i = 0; i < warmup; i++)
        _ = await embedder.EmbedAsync(corpus[i % corpus.Length], CancellationToken.None);

    var samples = new List<double>(timed);
    for (var i = 0; i < timed; i++)
    {
        var sw = Stopwatch.StartNew();
        _ = await embedder.EmbedAsync(corpus[i % corpus.Length], CancellationToken.None);
        sw.Stop();
        samples.Add(sw.Elapsed.TotalMilliseconds);
    }

    return samples;
}

// =================================================================================================
// Mode: latency
// =================================================================================================
async Task<int> RunLatencyMode(string[] modeArgs)
{
    var processStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    string ReadLoadAverage() => File.Exists("/proc/loadavg")
        ? string.Join(' ', File.ReadAllText("/proc/loadavg").Split(' ').Take(3))
        : "unavailable (non-Linux host)";
    var loadAverageBefore = ReadLoadAverage();

    var modelId = modeArgs.Length > 0 ? modeArgs[0] : new Netclaw.Configuration.MemoryEmbeddingsConfig().ModelId;
    var modelDir = modeArgs.Length > 1 ? modeArgs[1] : DefaultDirFor(modelId);

    Console.WriteLine($"Model id: {modelId}");
    Console.WriteLine($"Model directory: {modelDir}");
    ReportRss("before load");

    var loadOnlySw = Stopwatch.StartNew();
    var embedder = await LoadVerifiedAsync(modelId, modelDir);
    _ = await embedder.EmbedAsync(shortQueries[0], CancellationToken.None);
    loadOnlySw.Stop();
    var processToFirstEmbedMs = (DateTime.UtcNow - processStartUtc).TotalMilliseconds;

    Console.WriteLine();
    Console.WriteLine($"Cold load — process start -> first embed complete: {processToFirstEmbedMs:F1} ms (includes .NET host/runtime startup)");
    Console.WriteLine($"Cold load — LoadAsync + first embed only: {loadOnlySw.Elapsed.TotalMilliseconds:F1} ms");
    ReportRss("after cold load");

    var rows = new List<Row>
    {
        Percentiles("short", await RunCorpus(embedder, shortQueries, WarmupIterations, TimedIterations)),
        Percentiles("medium", await RunCorpus(embedder, mediumCorpus, WarmupIterations, TimedIterations)),
        Percentiles("doc", await RunCorpus(embedder, docCorpus, WarmupIterations, TimedIterations)),
    };
    ReportRss("after short+medium+doc timed runs");

    async Task<List<double>> RunConcurrentLoop(int iterations)
    {
        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = await embedder.EmbedAsync(shortQueries[i % shortQueries.Length], CancellationToken.None);
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        return samples;
    }

    var concurrencySw = Stopwatch.StartNew();
    var concurrentResults = await Task.WhenAll(RunConcurrentLoop(ConcurrencyIterationsPerLoop), RunConcurrentLoop(ConcurrencyIterationsPerLoop));
    concurrencySw.Stop();
    var concurrentSamples = concurrentResults[0].Concat(concurrentResults[1]).ToList();
    rows.Add(Percentiles("short (concurrency=2)", concurrentSamples));

    Console.WriteLine();
    Console.WriteLine($"Concurrency-2 pass total wall time: {concurrencySw.Elapsed.TotalMilliseconds:F1} ms for {concurrentSamples.Count} total calls (2x{ConcurrencyIterationsPerLoop})");

    // Steady-state RSS after a further burst of short queries only (mirrors "queries following
    // whatever the doc/medium runs above already allocated" — the shape most relevant to
    // ongoing query-embedding traffic once the ORT arena has seen its largest shapes).
    for (var i = 0; i < 200; i++)
        _ = await embedder.EmbedAsync(shortQueries[i % shortQueries.Length], CancellationToken.None);
    ReportRss("steady-state (after extra short-query burst)");

    Console.WriteLine();
    PrintRows(rows);

    Console.WriteLine();
    Console.WriteLine($"Load average before run (1m 5m 15m): {loadAverageBefore}");
    Console.WriteLine($"Load average after run  (1m 5m 15m): {ReadLoadAverage()}");
    Console.WriteLine();
    Console.WriteLine($"Peak RSS (VmHWM) for this process: {PeakRssMb()} MB");

    embedder.Dispose();
    return 0;
}

// =================================================================================================
// Mode: parity
// =================================================================================================
async Task<int> RunParityMode(string[] modeArgs)
{
    var fp32Id = "snowflake-arctic-embed-m";
    var int8Id = modeArgs.Length > 2 ? modeArgs[2] : "snowflake-arctic-embed-m-int8";
    var fp32Dir = modeArgs.Length > 0 ? modeArgs[0] : DefaultDirFor(fp32Id);
    var int8Dir = modeArgs.Length > 1 ? modeArgs[1] : DefaultDirFor(int8Id);

    var perSentenceProbe = shortQueries.Concat(docCorpus).ToArray(); // 20 short + 20 doc-like = 40
    var pairSentences = nearDupPairs.SelectMany(p => new[] { p.A, p.B })
        .Concat(unrelatedPairs.SelectMany(p => new[] { p.A, p.B }))
        .ToArray(); // 20 + 10 = 30
    var allSentences = perSentenceProbe.Concat(pairSentences).Distinct(StringComparer.Ordinal).ToArray();

    Console.WriteLine($"Probe set: {perSentenceProbe.Length} per-sentence + {nearDupPairs.Length} near-dup pairs + {unrelatedPairs.Length} unrelated pairs ({allSentences.Length} distinct sentences)");

    Dictionary<string, float[]> vectorsFor(OnnxMemoryEmbedder embedder)
        => allSentences.ToDictionary(s => s, s => embedder.EmbedAsync(s, CancellationToken.None).AsTask().GetAwaiter().GetResult().ToArray(), StringComparer.Ordinal);

    Console.WriteLine();
    Console.WriteLine("Loading fp32 model...");
    var fp32Embedder = await LoadVerifiedAsync(fp32Id, fp32Dir);
    var fp32Vectors = vectorsFor(fp32Embedder);
    fp32Embedder.Dispose();

    Console.WriteLine("Loading int8 model...");
    var int8Embedder = await LoadVerifiedAsync(int8Id, int8Dir);
    var int8Vectors = vectorsFor(int8Embedder);
    int8Embedder.Dispose();

    float Cosine(float[] a, float[] b) => TensorPrimitives.Dot((ReadOnlySpan<float>)a, (ReadOnlySpan<float>)b);

    // (a) Per-sentence parity: cosine(fp32_vec, int8_vec) for the same sentence.
    var perSentence = perSentenceProbe.Select(s => (Sentence: s, Cosine: Cosine(fp32Vectors[s], int8Vectors[s]))).ToArray();
    Console.WriteLine();
    Console.WriteLine("(a) Per-sentence parity, cosine(fp32_vec, int8_vec):");
    foreach (var (sentence, cosine) in perSentence)
    {
        var preview = sentence.Length > 60 ? sentence[..60] + "..." : sentence;
        Console.WriteLine($"  {cosine:F6}  \"{preview}\"");
    }
    var minParity = perSentence.Min(r => r.Cosine);
    var meanParity = perSentence.Average(r => r.Cosine);
    Console.WriteLine($"  min={minParity:F6} mean={meanParity:F6}  (gate: mean >= 0.99)");

    // (b) Ranking preservation: pair-cosine under fp32 vs under int8, for near-dup and unrelated pairs.
    Console.WriteLine();
    Console.WriteLine("(b) Ranking preservation — pair-cosine(fp32) vs pair-cosine(int8):");
    var pairDeltas = new List<double>();

    void ReportPairs(string label, (string A, string B)[] pairs, List<float> fp32Out, List<float> int8Out)
    {
        Console.WriteLine($"  {label}:");
        foreach (var (a, b) in pairs)
        {
            var fp32Cos = Cosine(fp32Vectors[a], fp32Vectors[b]);
            var int8Cos = Cosine(int8Vectors[a], int8Vectors[b]);
            var delta = Math.Abs(fp32Cos - int8Cos);
            pairDeltas.Add(delta);
            fp32Out.Add(fp32Cos);
            int8Out.Add(int8Cos);
            var aPreview = a.Length > 45 ? a[..45] + "..." : a;
            var bPreview = b.Length > 45 ? b[..45] + "..." : b;
            Console.WriteLine($"    fp32={fp32Cos:F4} int8={int8Cos:F4} delta={delta:F4}  \"{aPreview}\" <-> \"{bPreview}\"");
        }
    }

    var nearDupFp32 = new List<float>();
    var nearDupInt8 = new List<float>();
    var unrelatedFp32 = new List<float>();
    var unrelatedInt8 = new List<float>();
    ReportPairs("near-duplicate pairs", nearDupPairs, nearDupFp32, nearDupInt8);
    ReportPairs("unrelated pairs", unrelatedPairs, unrelatedFp32, unrelatedInt8);

    var maxDelta = pairDeltas.Max();
    Console.WriteLine($"  max pair-cosine delta across all {pairDeltas.Count} pairs: {maxDelta:F4}  (gate: <= 0.02)");

    Console.WriteLine();
    Console.WriteLine("(b-continued) Near-dup vs unrelated separation (nominator threshold reference: NominatorSimilarityThreshold=0.86):");
    Console.WriteLine($"  near-dup   fp32: min={nearDupFp32.Min():F4} mean={nearDupFp32.Average():F4}");
    Console.WriteLine($"  near-dup   int8: min={nearDupInt8.Min():F4} mean={nearDupInt8.Average():F4}");
    Console.WriteLine($"  unrelated  fp32: max={unrelatedFp32.Max():F4} mean={unrelatedFp32.Average():F4}");
    Console.WriteLine($"  unrelated  int8: max={unrelatedInt8.Max():F4} mean={unrelatedInt8.Average():F4}");
    var separationHolds = nearDupInt8.Min() > unrelatedInt8.Max();
    Console.WriteLine($"  separation holds under int8 (min near-dup > max unrelated): {separationHolds}");

    return 0;
}

// =================================================================================================
// Mode: arena — bench-only parallel embed path with configurable SessionOptions, mirroring
// OnnxMemoryEmbedder's tokenize/bucket/CLS-pool/L2-normalize logic exactly, so the *only*
// difference between combinations under test is the SessionOptions being investigated.
// =================================================================================================
async Task<int> RunArenaMode(string[] modeArgs)
{
    if (modeArgs.Length < 1)
    {
        Console.Error.WriteLine("Usage: arena <modelId> [modelDir] [arenaOn|arenaOff] [patternOn|patternOff]");
        return 1;
    }

    var modelId = modeArgs[0];
    var modelDir = modeArgs.Length > 1 ? modeArgs[1] : DefaultDirFor(modelId);
    var enableArena = modeArgs.Length > 2 ? modeArgs[2] == "arenaOn" : true;
    var enablePattern = modeArgs.Length > 3 ? modeArgs[3] == "patternOn" : true;

    Console.WriteLine($"Model id: {modelId}, dir: {modelDir}");
    Console.WriteLine($"SessionOptions: EnableCpuMemArena={enableArena}, EnableMemoryPattern={enablePattern}");
    ReportRss("before load");

    using var httpClient = new HttpClient();
    var provisioner = new EmbeddingModelProvisioner(httpClient, EmbeddingModelProvisioner.Allowlist);
    var verified = await provisioner.TryLoadVerifiedAsync(modelId, modelDir);
    if (verified is null)
    {
        Console.Error.WriteLine($"STOP: '{modelDir}' does not contain a hash-verified '{modelId}'. This tool never downloads.");
        return 1;
    }

    using var sessionOptions = new SessionOptions
    {
        IntraOpNumThreads = 4,
        EnableCpuMemArena = enableArena,
        EnableMemoryPattern = enablePattern,
    };
    using var session = new InferenceSession(verified.ModelPath, sessionOptions);
    var tokenizer = new BertTokenizer();
    await tokenizer.LoadVocabularyAsync(verified.VocabPath, convertInputToLowercase: true);
    var outputName = session.OutputMetadata.Keys.Single();
    var dims = verified.Dimensions;

    int ComputeBucketedLength(int actualLength, int bucket, int maxTokens)
    {
        var rounded = ((actualLength + bucket - 1) / bucket) * bucket;
        return Math.Clamp(rounded, bucket, maxTokens);
    }

    float[] EmbedOne(string text)
    {
        var scratchIds = new long[MaxTokens];
        var scratchMask = new long[MaxTokens];
        var scratchTypes = new long[MaxTokens];
        tokenizer.Encode(text, scratchIds, scratchMask, scratchTypes, MaxTokens);

        var actualLength = (int)scratchMask.Sum();
        var bucketLength = ComputeBucketedLength(actualLength, SequenceLengthBucket, MaxTokens);

        var inputIds = scratchIds[..bucketLength];
        var attentionMask = scratchMask[..bucketLength];
        var tokenTypeIds = scratchTypes[..bucketLength];

        var available = new Dictionary<string, NamedOnnxValue>(StringComparer.Ordinal)
        {
            ["input_ids"] = NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [1, bucketLength])),
            ["attention_mask"] = NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [1, bucketLength])),
            ["token_type_ids"] = NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, [1, bucketLength])),
        };

        var feed = new List<NamedOnnxValue>(session.InputMetadata.Count);
        foreach (var inputName in session.InputMetadata.Keys)
            feed.Add(available[inputName]);

        using var outputs = session.Run(feed);
        var lastHiddenState = outputs.First(o => o.Name == outputName).AsTensor<float>();
        var vector = new float[dims];
        for (var d = 0; d < dims; d++)
            vector[d] = lastHiddenState[0, 0, d];
        var norm = TensorPrimitives.Norm((ReadOnlySpan<float>)vector);
        if (norm > 0f)
            TensorPrimitives.Divide(vector, norm, vector);
        return vector;
    }

    List<double> RunPhase(string[] corpus, int count)
    {
        var samples = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = EmbedOne(corpus[i % corpus.Length]);
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        return samples;
    }

    // Warmup (not measured): prime JIT and any per-shape caches at a small scale first, including
    // a few doc-shaped inputs so the doc-burst measurement below isn't paying first-shape cost.
    RunPhase(shortQueries, 10);
    RunPhase(docCorpus, 5);
    ReportRss("after warmup");

    // Doc burst: simulates a backfill / bulk-write RAM spike — largest shapes this session sees.
    // Three passes over the 20-doc corpus (60 samples) for stabler percentiles — a single 20-item
    // pass proved noisy run-to-run on this box (median swung ~360-600ms for the identical config).
    var docSamples = RunPhase(docCorpus, docCorpus.Length * 3);
    ReportRss("after doc burst (60 embeds, ~440 tok each)");

    // Steady-state queries following the burst: this is the RSS number that matters for a
    // long-running daemon process — did the doc burst's memory come back down, or is it retained?
    var querySamples = RunPhase(shortQueries, 200);
    ReportRss("after 200 short queries following the doc burst");

    Console.WriteLine();
    PrintRows([Percentiles("doc burst", docSamples), Percentiles("queries after burst", querySamples)]);
    Console.WriteLine();
    Console.WriteLine($"Peak RSS (VmHWM) for this process: {PeakRssMb()} MB");

    return 0;
}

// =================================================================================================
// Dispatch
// =================================================================================================
var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "latency";
var modeArgs = args.Skip(1).ToArray();

return mode switch
{
    "latency" => await RunLatencyMode(modeArgs),
    "parity" => await RunParityMode(modeArgs),
    "arena" => await RunArenaMode(modeArgs),
    _ => Fail(mode),
};

int Fail(string unknownMode)
{
    Console.Error.WriteLine($"Unknown mode '{unknownMode}'. Expected: latency | parity | arena");
    return 1;
}

internal readonly record struct Row(string Label, int N, double P50, double P90, double P95, double P99, double Max, double Mean);
