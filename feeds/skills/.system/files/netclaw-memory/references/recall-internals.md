# Recall and Formation Internals

This reference is for operators and developers. It tells you how automatic
recall selects and ranks memories, and how memory formation finds duplicates.
The rules for your own behavior are in the skill, not here.

One rule applies to each section: when no candidate passes a floor or a gate,
recall puts nothing into the turn. That result is correct and healthy. Do not
tell the user that memory is broken.

## Hybrid Recall (semantic + lexical)

When `Memory.Embeddings.Enabled` is `true` (the default), automatic recall is
**hybrid**. The candidates are the union of full-text search (FTS5) and vector
nearest-neighbor search. One fused ranking then decides what recall puts into
the turn, if anything. When embeddings are disabled, recall is lexical-only:
the same candidate pool, with no vector term and no cosine floor.

- **Fusion**: the score of each candidate is `VectorWeight × cosine similarity +
  LexicalWeight × squashed lexical score`. The class prior adjusts the score.
  Then a **recency decay** applies: a half-life multiplier that prefers the
  fresher memory among similar candidates. Age alone never brings an old
  memory to zero.
- **Defaults**: `Memory.Recall.VectorWeight` is 0.7, `LexicalWeight` is 0.3,
  and `RecencyHalfLifeDays` is 30.
- **The query prefix is automatic for each model**: recall embeds the turn
  query with the retrieval-query encoding that the active embedding model
  documents. For the shipped default `snowflake-arctic-embed-m-int8`, and for
  the allowlisted fp32 `snowflake-arctic-embed-m` that it is quantized from,
  recall puts a fixed instruction string before the query text. This is a
  property of the model. You do not configure it. Stored memories
  (document-side embeddings) never get a prefix, so a prefix never makes it
  necessary to embed existing content again.
- **Absolute floor**: independent of the fused score, recall drops a candidate
  before the ranking when its raw cosine similarity is below the effective
  `MinCosineSimilarity`. If no candidate clears the floor, recall puts nothing
  into the turn.

### The cosine floor

- **By default the floor follows the manifest of the active model.**
  `Memory.Recall.MinCosineSimilarity` is nullable. It is `null` unless an
  operator sets an explicit override. When it is `null`, the effective floor is
  the calibration that is pinned to the active embedding model.
- The floor is 0.24 for the shipped default `snowflake-arctic-embed-m-int8`
  with the prefixed encoding. It is also 0.24 for the allowlisted fp32
  `snowflake-arctic-embed-m` with the prefixed encoding. The two values come
  from independent calibrations. On the same gold sets, int8 measured as a
  strict improvement in retrieval quality over fp32. It is not a trade of
  quality for size or latency.
- **The number has a meaning only for one model and one encoding.** Cosine
  distributions change materially between models. They also change for one
  model between a prefixed and an unprefixed encoding. An old value from
  another model or encoding can silently break recall. Measured example:
  F0.5 = 0.0 when the pre-prefix floor of 0.68 was applied to prefixed queries.
- Set an explicit override only after you run the calibration-verification
  procedure again, against the model and the encoding that are active.

### Degradation is explicit and logged

Recall never degrades silently. Each of these conditions repairs itself or is
intentional. None is a persistent failure. See `references/diagnostics.md` for
the log events.

- The query-embedding step misses its latency sub-budget, or no embedder is
  available. Recall uses lexical-only scores for that turn and logs
  `memory_recall_vector_degraded`.
- The model has no retrieval calibration in its manifest, and there is no
  explicit `MinCosineSimilarity` override. Recall degrades in the same way,
  with the reason `missing_calibration`. This is expected for a new model
  variant, or for one that is not calibrated yet. It is not a bug.
- A candidate has no embedding row for the current model. Recall uses a
  lexical-only score for that candidate alone, does not exclude it, and logs
  `memory_recall_coverage_gap`.

### Backfill and model changes

- **Backfill of an existing corpus**: when an operator enables
  `Memory.Embeddings.Enabled` on a deployment that already has memories,
  Netclaw does not embed the old memories at once. Until they are embedded,
  recall for those documents degrades to lexical scores, and
  `memory_recall_coverage_gap` continues to fire. Run
  `netclaw memory backfill-embeddings` immediately after you enable
  embeddings. Then the gap closes at once, and does not wait for
  embed-on-write.
- **Upgrade to a new default model ID** (for example the change of the default
  from fp32 to int8): an installation with vectors under the previous
  `Memory.Embeddings.ModelId` repairs itself. Vector coverage, the curation
  nominator, and hybrid recall all use only the *current* model ID. At
  startup, the gap-repair sweep of the daemon finds that each document has no
  embedding for the current model, and embeds the full corpus under the new
  ID. No operator action is necessary. Netclaw never deletes the vectors of
  the old model. It only stops reading them. Until the gap repair completes,
  recall degrades to lexical-only, with the same logged degradation as any
  other coverage gap. During that time, `netclaw doctor` shows the mixed-model
  state as a warning and recommends
  `netclaw memory backfill-embeddings --force` to do the repair immediately.

## Relevance Gate (cross-encoder)

The cosine floor answers the question "is this candidate on-topic?". It does
not answer "does this candidate help to answer the question?". For that
reason a second stage, the **relevance gate**, runs after the floor. A tiny
cross-encoder (`ms-marco-minilm-l-6-v2`) scores `(query, candidate)` jointly
for each of the floor survivors (3 at most). The gate drops each candidate
below its calibrated threshold.

- **Activation follows `Memory.Embeddings.Enabled`**: one switch, with no
  second setting to find. `Memory.Recall.RelevanceGate.Enabled` (nullable) is
  an explicit override. It is for an operator who wants embeddings for dedup
  and hybrid recall, but not the added cross-encoder latency on each turn.
  `Memory.Recall.RelevanceGate.Threshold` (nullable) is an explicit override
  of the calibrated operating point of the manifest. Keep both `null` unless
  you have a specific reason. The manifest default is the value that was
  validated out-of-sample.
- **The gate runs only in hybrid mode**, and only on the survivors of the
  floor. It never gets a wider candidate pool. It never runs after recall
  degraded to lexical-only.
- **Zero survivors after the gate is a healthy result**, the same as zero
  survivors at the floor. Recall omits the `[memory-recall]` block fully. It
  does not emit an empty block.
- **Degradation is explicit and logged.** When the relevance model is
  unavailable, when its sub-budget is exceeded, or when recall runs in lexical
  mode, recall skips the gate step and uses the result of the floor without a
  filter. That is the exact behavior before the gate existed. Recall logs
  `memory_recall_gate_degraded`, rate-limited with the same cooldown pattern
  as `memory_recall_vector_degraded`. A degraded gate never changes what
  recall shows without this marker.

The operator runbook has the gate health checks and the degradation reasons:
<https://github.com/netclaw-dev/netclaw/blob/dev/docs/runbooks/memory-health-and-evals.md>
("Relevance Gate Health").

## Memory Formation

- **Duplicate detection is semantic when embeddings are enabled**
  (`Memory.Embeddings.Enabled`). Embedding similarity nominates a
  near-duplicate proposal. The curator LLM then decides: skip, update,
  consolidate, or create. Similarity alone never merges or skips a memory.
- A merge is lossless-or-append. The curator writes a merged body that keeps
  each source fact. If that check fails, a deterministic guard appends the
  proposal and does not overwrite the memory.
- **Automatic observation and adopted context**:
  - An adopted thread window that is not empty still counts as adopted context
    for audit and for approval provenance.
  - Automatic memory suppression applies only when the adopted window includes
    a sender other than the current authorized author.
  - Adopted history from the author alone does not suppress automatic memory
    formation.
