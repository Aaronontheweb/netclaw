# Design: memory-core-redesign

## Context

The July 2026 audit (`docs/research/memory-audit-2026-07.md`) measured the
memory system end-to-end against the live 1,216-document corpus: 46% of
auto-injected memories are pollution (relevance judged, κ=0.754); the lexical
composite score carries no relevance signal (precision flat across every
floor); the LLM curation tier had **zero** successful decisions in its
production lifetime; consolidation fired twice ever while redundancy reached
14% and doubled in five weeks; the checkpoint worker drops 95% of its intake;
`Searchable` recall mode secretly participates in automatic recall; the Trace
class and expiry mechanics are fully vestigial (0 traces ever, no deletion,
204 expired records mummified on disk); `memory_edges` has 0 rows and the
facet planner knows 4 demo facets against ~900 real ones.

The quick-win slice (PR #1568) revived the LLM tier, adopted the balanced
curation prompt, re-tuned lexical scoring, and added an injection budget.
This change is the structural remainder: add the missing **judgment**
(embeddings), add the missing **metabolism** (lifecycle: lossless merge,
expiry, consolidation), and **subtract** the dead structure that three
redesign cycles accreted. Prior art constraints come from the May 2026
autoresearch (`docs/research/memory-recall-findings-2026-05.md`): nominate-by-cosine/decide-by-LLM is
ratified (no cosine threshold separates duplicates from siblings — siblings
live at 0.905–0.941 inside the duplicate band), and the nominator model is
snowflake-arctic-embed 137M (33M-class models measured inadequate for
doc-to-doc dedup).

Actor/persistence context: memory lives in the daemon's single SQLite file
(`NetclawPaths.MemorySqliteDbPath == SqliteDbPath`), whose memory tables are
owned by `SQLiteMemoryStore.InitializeAsync` (idempotent DDL), NOT by the
daemon's `SchemaMigrator`. Two write pipelines exist today: the inline
per-session path (`SessionMemoryObserverActor` → `MemoryProposalGate` →
`MemoryCurationActor`, a per-session child of `LlmSessionActor`) and the
daemon checkpoint worker (`MemoryCurationWorkerService` →
`MemoryCurationEngine`). Recall runs on the session actor's turn path under a
hard latency budget (`Memory.RecallTimeoutMs`, default 300 ms).

## Goals / Non-Goals

**Goals**

1. Fewer, more comprehensive memories: near-duplicates are detected
   semantically at write time and merged losslessly.
2. Fewer, more accurate injections: recall is gated by an absolute semantic
   relevance floor; most turns inject nothing (measured correct outcome for
   65% of real queries).
3. A real lifecycle: expired rows are deleted, redundant clusters are
   consolidated under operator control, short-lived (≈72 h) memories exist
   and work.
4. Tool-use lessons are captured and surface exactly when the relevant tool
   is used.
5. Less machinery: taxonomy and pipelines shrink to the behaviors that
   actually exist; every metadata field written is consumed by a reader.

**Non-Goals**

- Multilingual embeddings (model swap later; vectors keyed by `model_id`).
- ANN indexes (brute force is sub-ms at this corpus scale; revisit ≥50k).
- Automatic (code-level) detection of tool-use corrections.
- Applying consolidation to any corpus as part of implementation (tooling
  ships; each apply run is an operator decision).
- Multi-node/cluster memory; this remains single-process MVP.
- Remote embedder mode (calling an OpenAI-compatible `/v1/embeddings`
  endpoint instead of in-process ONNX) — deferred future path for
  memory-constrained fleets that need embedding RAM off the daemon's own
  pod entirely, per operator direction (Slice 4 Stage A: in-process int8
  quantization got RAM down substantially but failed its quality gate as a
  default — see D2/D6 — so a remote option remains the lever for fleets
  that can't accept either the fp32 RAM cost or the int8 quality tradeoff).

## Decisions

### D1. Embedding runtime: in-process ONNX, new `src/Netclaw.Embeddings` project

`Microsoft.ML.OnnxRuntime` (CPU EP; linux-x64 + linux-arm64 ship in 1.25+) +
`FastBertTokenizer` (pure managed WordPiece — the chosen model is BERT-class)
+ `System.Numerics.Tensors` for SIMD cosine. The consumer-defined seam
`IMemoryEmbedder` lives in `Netclaw.Actors/Memory` so actor code never
references OnnxRuntime; `Netclaw.Embeddings` is referenced by Daemon and CLI
only. A singleton `OnnxMemoryEmbedder` holds one `InferenceSession`
(`IntraOpNumThreads` bounded, concurrency semaphore ≤2); an
`UnavailableMemoryEmbedder` stub carries `IsAvailable=false` for degraded
mode.

*Alternative considered*: Ollama sidecar — rejected: violates the
single-process constitution, adds a network hop inside the recall budget, and
creates a second silent-failure surface. *Alternative*: embedding via the
existing chat-provider plugins — rejected: recall must work when no provider
is reachable, and provider embedding APIs are not uniformly available.

### D2. Model provisioning: pinned allowlist, download at initialization, never embedded

`Memory.Embeddings.ModelId` selects from an **in-code allowlist manifest**
(model id → URL + byte size + SHA-256); arbitrary URLs are rejected
(supply-chain boundary). An `EmbeddingWarmupHostedService` provisions at
daemon start when `AutoDownload=true` (atomic temp+rename download, hash
verify, then one warm-up inference), or the operator runs
`netclaw memory backfill-embeddings`. The ~90–140 MB artifact is never an
embedded resource (would bloat every RID publish). Default model:
snowflake-arctic-embed-m (~110M params, fp32 ONNX, pinned by hash;
May-ratified), mxbai-embed-large 335M is the allowlisted fallback.
Post-PoC decision deferred: mirroring artifacts into the existing R2 feeds
channel vs pinned upstream URLs.

**Slice 4 Stage A: int8 quantization evaluated, quality gate failed, fp32
stays the default.** The operator-set goal was RAM lean enough that enabling
the in-process embedder is a non-decision inside a 1 GB pod. HuggingFace's
`Snowflake/snowflake-arctic-embed-m` repo (same pinned commit
`fc74610d18462d218e312aa986ec5c8a75a98152`) publishes several pre-quantized
ONNX artifacts under `onnx/`: `model_fp16.onnx` (218 MB), `model_int8.onnx`
and `model_quantized.onnx` (byte-identical to each other, 110 MB, signed
QInt8 weights — the "s8s8" scheme), `model_uint8.onnx` (110 MB, unsigned
QUInt8 weights — the "u8u8" scheme, a genuinely different artifact with a
different pinned hash, not an alias), `model_q4.onnx` (149 MB, 4-bit), and
`model_bnb4.onnx` (144 MB, bitsandbytes 4-bit). `model_uint8.onnx` was added
to the allowlist as `snowflake-arctic-embed-m-int8`
(SHA-256 `4cfc22160ddd52bac43697b6b84a4b29ea25a82db23841c27436dbddcfd5f88a`,
110,084,023 bytes) — QUInt8 is ORT's portable-across-architectures dynamic
quantization scheme (matches what
`onnxruntime.quantization.quantize_dynamic(weight_type=QUInt8)` would produce
locally) and avoids the accuracy/VNNI caveats ORT's own docs attach to signed
QInt8 on non-VNNI hardware.

Quality was validated against fp32, not assumed (`tools/embed-latency-bench`
`parity` mode: 20 short queries + 20 doc-like paragraphs, per-sentence
cosine(fp32, int8); 10 near-duplicate pairs + 5 unrelated pairs, pair-cosine
delta). Both `model_uint8.onnx` and the signed `model_int8.onnx` sibling were
measured (the latter as a one-off comparison, not allowlisted) — same
shortfall, so the choice between the two 8-bit schemes was not the problem:

| metric (int8 = model_uint8.onnx)              | measured  | gate    | result |
|------------------------------------------------|-----------|---------|--------|
| per-sentence parity, mean cosine(fp32,int8)     | 0.9829    | ≥ 0.99  | **FAIL** |
| per-sentence parity, min cosine                | 0.9609    | —       | (informational) |
| — short queries only (20)                       | 0.991–0.996 | —     | passes alone |
| — doc-like paragraphs only (20)                 | 0.953–0.981 | —     | fails alone |
| max pair-cosine delta (10 near-dup + 5 unrelated)| 0.0231    | ≤ 0.02  | **FAIL** (barely) |
| near-dup min cosine (int8) vs unrelated max (int8) | 0.8598 vs 0.6326 | separation holds | pass |

(For reference, the signed `model_int8.onnx` scored mean parity 0.9807, min
0.9535, max pair delta 0.0191 — the pair-delta gate passes by that scheme but
per-sentence mean parity still fails; not a better overall choice.)

The failure is systematic and length-correlated, not noise: every one of the
20 short queries (13–20 tokens) parity above 0.99; every one of the 20
doc-like paragraphs (~440 tokens) parity below 0.99, in a tight band
(0.953–0.981) — 8-bit quantization error compounds across the transformer's
attention computation as sequence length grows. The nominator threshold's
discriminative power is *not* at risk (near-dup vs unrelated separation holds
comfortably under int8 too, so `NominatorSimilarityThreshold=0.86` would not
need recalibration if int8 shipped), but the acceptance gate for *shipping a
new default* is per-sentence parity, and that gate fails.

Per the "no silent fallbacks" / "get the score up, don't move the goalposts"
rule: **the default stays fp32 `snowflake-arctic-embed-m`.**
`snowflake-arctic-embed-m-int8` remains allowlisted as an explicit,
documented opt-in for operators who can tolerate degraded doc-embedding
quality in exchange for RAM (see the RSS/latency table under D6 — it is not
a non-decision, but it is an available lever). Revisiting this is future
work: either a differently-calibrated (static, not dynamic) quantization
method, or accepting the current tradeoff deliberately per-deployment. See
also the Non-Goals note below on remote embedder mode as the alternative
lever for memory-constrained fleets that don't want any in-process quality
tradeoff at all.

**Slice 4 follow-up (2026-07-05): gold-metrics quantization verdict — the
parity gate was a proxy, and on real retrieval the proxy over-rejects.** The
Stage A gate above compares vectors to fp32; this measurement asked the
question the gate stands in for: does quantization change *retrieval
outcomes* on the operator's actual gold sets? Offline eval against the real
1,216-doc corpus clone: full-corpus cosine ranking on the real-traffic gold
set (`gold-prod-2026-07`, 33 scored queries) and the May synthetic held-out
split (`gold-repooled-test`, 22 scored), plus nominator twin-recall over the
267 ratified near-dup pairs (259 positives; k=5, τ=0.86, full-corpus
competitors) and the ratified sibling/hard-negative must-not-merge pairs.
Embedding semantics replicated production exactly (`OnnxMemoryEmbedder`:
tokenizer.json WordPiece lowercase, CLS pooling, L2 norm, bucket-of-8
dynamic length, `title\nbody` composition, IntraOpNumThreads=4). Artifacts:
the same pinned HF revision as the allowlist; `model_uint8.onnx` SHA-256
matches the allowlisted `snowflake-arctic-embed-m-int8` entry;
`model_fp16.onnx` SHA-256
`7f8fcebda72ae4eec54769f42727c5c7484b271358d423bf610db7327093cb08`.

| variant | MRR gold-prod (Δ) | MRR repooled-test (Δ) | recall@5 g-p / r-t (Δ) | twin-recall pair@τ0.86 → @τ′ | sibling separation | RSS steady/peak (eval harness) | corpus embed (1,216 docs) |
|---------|------------------:|----------------------:|------------------------|------------------------------|--------------------|--------------------------------|--------------------------:|
| fp32 (reference) | 0.2011 | 0.4665 | 0.1465 / 0.3477 | 32.8% (τ′=τ) | intact (margin 0.022) | 611 / 611 MB | 81.2 s |
| fp16 | 0.2011 (±0.0000) | 0.4665 (±0.0000) | = / = | 32.8% → 32.8% (τ′≈0.860) | intact (≡fp32) | 948 / 951 MB | 128.0 s — **crashes at production session options** |
| uint8 (int8 allowlist entry) | 0.2041 (+0.0030) | 0.4360 (−0.0306) | +0.0101 / −0.0341 | 23.6% → **32.0%** (τ′=0.845) | intact, margin halved (0.011) | **261 / 263 MB** | **66.3 s** |

Key findings behind the table (full numbers stay local in
`~/recall-research-local/2026-07/quant-eval/results.md` — corpus and gold
sets are PII):

- **fp16 is ruled out, hard.** ORT 1.27.0's CPU EP fails session
  initialization on `model_fp16.onnx` at the default graph-optimization
  level (`SimplifiedLayerNormFusion` references a missing
  `InsertedPrecisionFreeCast` node arg) — production's `SessionOptions`
  would crash at load. Under the only workaround (`ORT_ENABLE_EXTENDED`),
  fp16 exactly matches fp32 quality (max pair-cosine |Δ| 1.5e-4) while
  running 1.6× slower and using ~55% *more* RSS than fp32 (the CPU EP
  inserts casts and keeps both precision copies live). No axis favors it.
- **uint8 retrieval quality is within noise on real gold.** Paired
  bootstrap (5,000 resamples): gold-prod deltas are non-negative
  (MRR +0.0030, CI [−0.045, +0.058]); repooled-test dips ≤3.5pp with every
  CI touching or crossing zero except nDCG@5 at [−0.0742, −0.0002]. Mixed
  signs across sets, 22–33-query samples: no systematic ranking
  degradation detectable — the D2 vector-parity shortfall (doc-vector
  cosine to fp32 ≈0.974) does not surface as retrieval loss.
- **uint8 does shift the nominator's operating point, systematically.**
  Pair cosines compress downward (mean −0.0127, p95 |Δ| 0.035, max 0.051),
  so at the fixed τ=0.86 twin-recall silently drops 32.8%→23.6%
  (pair-AND, k=5). Quantile-matching fp32's pass-fraction gives
  **τ′ = 0.845**, which restores twin-recall to 32.0% (within 0.8pp of
  fp32). Sibling/hard-negative separation stays intact under τ′ (max
  sibling cos 0.835 < 0.845; hard-neg max 0.770; both measured sibling
  cosines moved *down* under uint8), but the hottest-sibling margin halves
  (0.022 → 0.011) — re-verify if τ is ever tuned upward.
- **Efficiency confirms the Stage A numbers** (python ORT harness vs the
  C# bench: 611 vs 636 MB fp32, 261 vs 276 MB int8 — same shape). int8 +
  daemon peak (397 MB) ≈ 660–675 MB against the 1 GB pod limit, vs
  ≈1033 MB for fp32-dynamic.

**Recommendation (evidence, not yet a decision-flip):** uint8 as the
default is now supportable on real-retrieval evidence, with one mandatory
condition — `NominatorSimilarityThreshold` must default to ~0.845 (not
0.86) whenever the int8 model is selected, i.e. the threshold default
becomes model-conditional. With that recalibration, every gold-metric this
corpus can express is at or within noise of fp32, and the operator's
1 GB-pod goal is met with >300 MB headroom instead of missed by 9 MB. If a
model-conditional τ default is judged too much config surface, fp32-dynamic
stands as the quality-reference default and int8 remains the documented
opt-in — but the D2 parity-gate framing of int8 as a *retrieval-quality*
downgrade is no longer supported by measurement; the real cost is a τ
recalibration plus a halved sibling margin. fp16 should never be
allowlisted for the CPU path.

### D3. Vector storage: separate `memory_embeddings` table, owned by the store

`memory_embeddings(item_id, item_kind, model_id, content_hash, dims, vector
BLOB, created_at, PRIMARY KEY(item_id, model_id))`, created in
`SQLiteMemoryStore.InitializeAsync` alongside the other memory DDL — not a
daemon migration, preserving the store's standalone-initialization contract
(doctor and tests construct it without the migrator). Content hash =
SHA-256 of normalized title+body; re-embed is skip-if-hash-match, so backfill
re-runs are free. Model change = new `model_id` rows + `--force` backfill; no
rewrite of the 224 MB documents table. Backfill state is **derived**
(LEFT JOIN on current model + hash), never a progress table. kNN executes as
a brute-force scan over an in-memory `MemoryVectorIndex` (flat float[] per
model, ~1.8 MB at current scale, invalidated by a store version counter). No
sqlite-vec/native extensions (ARM64 + deployment liability for zero benefit
at this scale).

*Failure/recovery*: a crash between document commit and embedding upsert
leaves a missing-embedding row; the warmup service's gap-repair sweep and the
embedding doctor check both surface and heal it. Vectors are derived data —
loss is always recoverable by re-embedding.

### D4. Write-side: one evaluator; kNN nominates, LLM decides; no cosine auto-merge

The duplicated evaluation logic in `MemoryCurationActor.EvaluateSingleAsync`
and `MemoryCurationEngine` collapses into one shared `MemoryCurationEvaluator`
used by both the inline actor and the daemon worker (today's guards diverge —
`GuardDestructiveUpdate` exists on one path only). Evaluation order:

1. Exact-anchor + near-identical body → deterministic SKIP (cheap fast path).
2. Embedding kNN nomination at `NominatorSimilarityThreshold` (default 0.86)
   / `NominatorK` (default 5). **Any nominee forces the LLM tier** — the May
   measurement stands: no cosine threshold separates duplicates from siblings,
   so cosine never auto-merges and never auto-skips.
3. No nominee and no anchor match → CREATE without an LLM call (the common
   case stays cheap; median nominee count on a random write is 0).
4. Embedder unavailable → the current lexical candidate search runs as the
   explicitly-logged degraded path.

*Alternative considered*: cosine auto-merge tier above 0.95 — rejected: the
measured sample shows ~3 pairs there, not worth a data-loss risk surface.

### D5. Lossless merge: LLM-synthesized body + deterministic MergeGuard + append fallback

CONSOLIDATE/UPDATE decisions now carry a merged body
(`CurationDecision.MergedBody`) synthesized by the curation LLM from
full-content previews. A deterministic `MergeGuard` validates it: load-bearing
tokens (URLs, numbers, versions, dates, code identifiers) from every source
body must survive (≥95%), and length must not collapse. On failure the write
degrades to a **structural append** (existing body + dated separator +
proposal — finally producing the `AppendDocument` semantics that have existed
unused since the enums were written). The raw
`markdown_body = excluded.markdown_body` overwrite becomes unreachable from
curation decisions. Records remain immutable and curation-bypassing.

*Why not prompt-only*: the May decider eval measured the balanced prompt at
~27% wrong-merge on hard near-duplicates. The guard turns a wrong merge from
silent data loss into recoverable over-consolidation.

### D6. Read-side: hybrid recall with an absolute cosine floor

Per turn: embed the query once (sub-budget inside `RecallTimeoutMs`; on
timeout or unavailable → lexical-only + rate-limited
`memory_recall_vector_degraded` log). Candidates = FTS5 top-k ∪ vector top-k,
deduplicated, **all candidates passing the identical policy gates**
(audience/boundary/sensitivity/recall-mode) regardless of source — a
correctness requirement with its own scenario. Scoring = weighted fusion
(`VectorWeight` 0.7 × cosine + `LexicalWeight` 0.3 × squashed selector score
+ dampened class prior), then an **absolute floor**: `MinCosineSimilarity`
(default 0.55, calibrated against the real-traffic gold set
`gold-prod-2026-07`). Nothing above the floor → inject nothing, and the
volatile `[memory-recall]` block is omitted entirely (zero tokens). Recency
decay (`RecencyHalfLifeDays`, floor-bounded multiplier) breaks ties toward
fresh knowledge. The quick-win char budget and `AutoRecallMaxItems` remain
the outer bounds.

*Alternative considered*: RRF fusion — rejected: rank-only fusion always
admits the top item even when nothing is relevant; the zero-injection
behavior requires an absolute score. *Latency measured, not assumed*: Ollama
measurements ran far above the 10–50 ms/query assumption, and the in-process
ONNX fp32 measurement (Slice 2 task 2.13; full numbers in Open Questions)
shows the same problem persists — p95 ≈ 315 ms on the i9-9900K reference box,
~2× over the 150 ms sub-budget, because the embedder pads every input to a
fixed 512 tokens regardless of actual length.

**Mitigation, measured (`tools/embed-latency-bench` dynamic-length
extension)**: the ONNX graph's sequence axis is symbolic
(`input_ids`/`attention_mask`/`token_type_ids` all declare
`[batch_size, sequence_length]`, no fixed shape), so padding to the actual
tokenized length (rounded up to a multiple of 8) instead of a fixed 512 is a
drop-in change — no re-export needed. On the same reference box: short-query
p50 **19.0 ms**, p95 **20.9 ms** (was p50 281.9 ms / p95 310.5 ms fixed-512 —
~15× faster, ~7× under the 150 ms sub-budget); medium (~178 tok) p50
**84.1 ms** (was 281.7 ms); doc-length (~442 tok) p50 **235.5 ms** (was
280.3 ms — smaller gain because 442 tokens is already close to 512).
Correctness parity across 10 fixed sentences (short queries + longer bank
sentences), fixed-512 vs dynamic-length, cosine similarity: **1.000000 on
every sentence** (min = mean = 1.000000) — the attention mask fully absorbs
the padding difference, so this is a pure performance change with no
retrieval-quality risk. **Decision: Slice 4 adopts dynamic sequence length
(bucket-of-8 rounding) as the query-embedding mitigation**, not int8
quantization and not a relaxed budget — the 150 ms sub-budget holds with
large headroom once padding is length-aware.

**Slice 4 Stage A: dynamic length moved into production, RAM measured
end-to-end.** The experiment above is no longer a bench-only parallel code
path — `OnnxMemoryEmbedder.EmbedOne` now pads to `bucket-of-8(actual
token count)` capped at 512 unconditionally (no flag; the parity measurement
above proved there is no scenario where fixed-512 is preferable). This also
answered the RAM question the fixed-512 measurement raised but didn't
answer: how much of the ~988 MB steady RSS was the model weights vs. an ORT
arena sized for a fixed 512-token workload. Full before/after, same
reference box (i9-9900K, 8 logical cores; this run was on a heavily shared
dev box — load average 85–600 during measurement, roughly 15–100× the
Slice 2 measurement's contention, so treat absolute latency digits as
directional and RSS as the trustworthy signal):

| build                        | RSS steady | RSS peak (VmHWM) | cold load | short p50/p95 | medium p50/p95 | doc p50/p95 |
|-------------------------------|-----------:|-----------------:|----------:|---------------:|----------------:|-------------:|
| fp32, fixed-512 (Slice 2 baseline, pre-Stage-A) | ~988 MB | ~998 MB | ~1069 ms | 281.9/310.5 ms | 281.7/312.2 ms | 280.3/304.6 ms |
| fp32, dynamic-length (Stage A, allowlisted alternative) | 636 MB | 636 MB | 2010 ms | 20.0/22.6 ms | 91.1/132.9 ms | 246.5/376.6 ms |
| **int8, dynamic-length (Stage A, allowlisted opt-in, not default — see D2)** | **276 MB** | **276 MB** | **726 ms** | **9.6/11.8 ms** | **69.4/81.3 ms** | **206.6/230.3 ms** |

fp32 + dynamic length alone drops steady RSS ~36% (988→636 MB) versus the
fixed-512 baseline purely from no longer forcing every ORT arena allocation
to a 512-token shape — a quality-neutral win that ships regardless of the
int8 decision. The int8 build (opt-in only; D2) would additionally drop RSS
~72% from the original baseline (988→276 MB) with faster latency across the
board, but does not ship as the default because its measured quality parity
against fp32 failed the acceptance gate (D2) — cold-load and steady RSS
above are for the record, not a claim that int8 is production-recommended.

**1 GB pod goal, current state**: daemon baseline measured ~265 MB RSS
steady / ~397 MB peak. fp32-dynamic (636 MB) + daemon peak (397 MB) ≈
1033 MB — essentially at the 1 GB line, not comfortably under it. Int8
(276 MB) + daemon peak ≈ 673 MB would clear it with a wide margin, but isn't
shippable as the default per the quality gate above. So Stage A gets
meaningfully closer to the 1 GB goal (dynamic length alone, quality-neutral)
without fully closing it; fully closing it needs either an accepted int8
quality tradeoff (operator opt-in, already available) or the deferred remote
embedder mode (see Non-Goals). *Update 2026-07-05*: the gold-metrics eval
recorded under D2 ("Slice 4 follow-up") measured int8 retrieval quality at
parity-within-noise on the real gold sets — the int8-as-default option now
rests on a τ recalibration (0.86 → ~0.845), not a retrieval-quality
tradeoff; see that section before treating fp32-as-default as settled.

**Arena tuning (task A4), measured**: `tools/embed-latency-bench`'s `arena`
mode ran a mixed doc-burst-then-queries workload (proper warmup, 60 doc-shaped
+ 200 query-shaped timed samples per combination) against all four
`SessionOptions` combinations of `EnableCpuMemArena` × `EnableMemoryPattern`
on the int8 build. Result: ORT's defaults (both `true`) won or tied on every
axis measured — disabling `EnableMemoryPattern` alone regressed doc-embedding
p95 from 337 ms to 727 ms (≈2×) for a ~3% RSS saving (284→275 MB peak);
disabling `EnableCpuMemArena` (either combination) *increased* peak RSS to
303–312 MB while also increasing doc-embedding latency. No combination was a
clear win at the constitution's ≤10% latency-cost bar, so **the defaults are
kept, unmodified, with a comment recording this measurement** rather than
exposed as a new config knob (`OnnxMemoryEmbedder.LoadAsync`).

### D7. Taxonomy rebalance: recall modes mean what they say

- **BREAKING (semantic fix)**: `Searchable` leaves the automatic recall pool
  (`SearchByPlanAsync` admits `auto` only). `Searchable` = find_memories
  surface; `Manual` = explicit-id access; `Never` = policy-hidden. The 22
  legacy compaction rows were already repaired in the quick-win slice; a
  startup data-repair re-asserts invariants idempotently.
- Formation: the observer sidecar proposes a recall mode; the policy gate
  honors it for durable facts with **default `searchable`** — `auto` is
  reserved for standing facts that should color every conversation (identity,
  durable preferences, environment). This breaks the measured 97%-auto
  monoculture at the source. The distillation prompt is rewritten for fewer,
  more comprehensive proposals (consolidate related observations into one
  document; propose fewer atomic fragments).
- **Trace revival**: the sidecar may propose `trace` (short-lived operational
  state, TTL 72 h) — the class becomes reachable, recallable while fresh
  (recall mode `auto` with its TTL as the guard, weighted below durable
  facts), and actually deleted by the expiry sweep (D8).
- **Tool lessons**: new `MemoryClass.ToolLesson` → Document/MergeDocument/
  Searchable, anchored `anchor_type="tool"`, `canonical_name=<tool>`.
  Captured explicitly (`store_memory` accepts the class; the `netclaw-memory`
  skill instructs saving a lesson when the user corrects tool usage) and by
  the sidecar distillation prompt (correction-hunting instruction). Recall is
  **per-tool context injection**: on a tool's first use in a session, the
  tool-execution pipeline appends a compact `[tool-lessons:<name>]` block
  (top 2 by `updated_at`, bounded chars) to the tool result — an exact
  anchor-id lookup, no embedding, outside the pre-turn recall budget, reset
  on compaction. The dead `verified-tool-finding` +25 recall bonus is
  removed; `store_memory` with the class becomes the first real producer of
  the `VerifiedToolFinding` checkpoint flag.

*Alternative considered*: overloading Evidence for lessons — rejected:
Evidence is policy-forced to immutable Record + searchable, so lessons could
never be refined by curation and would never surface unprompted.

### D8. Metabolism: expiry sweep + operator-gated consolidation

- **Expiry sweep**: a daemon maintenance step (piggybacking the checkpoint
  worker's idle loop) DELETEs rows whose `expires_at` has passed beyond a
  grace window — they are already invisible to every read path, so deletion
  is behavior-neutral by construction; each sweep logs counts. (Audit: 204 of
  384 evidence records currently mummified.)
- **Consolidation**: `netclaw memory consolidate --dry-run` builds the kNN
  cluster graph, runs the merge-synthesis prompt per cluster, and writes a
  human-editable `plan.jsonl` + report — no mutation. `--apply --plan <path>`
  executes a reviewed plan verbatim: refuses a live daemon by default, takes
  a `VACUUM INTO` backup first, applies in batched transactions, re-embeds
  merged bodies, rebuilds FTS rows, and records a `memory_maintenance_runs`
  ledger row. `netclaw memory status` reports class/recall-mode/embedding
  coverage. CLI-owned rather than a daemon job because the ratification gate
  is inherently interactive.

### D9. Subtraction

Removed with evidence they carry no load (audit): `memory_edges` table and
its DDL/spec requirement (0 rows ever; anchors remain as flat grouping keys);
the facet/soft-scope *inference* in `DeterministicRetrievalPlanning` (4
hardcoded demo facets; stopword-hygiene and lexical-term extraction remain);
the checkpoint worker's unconditional turn-complete enqueue (gated at enqueue
by the same project-fact precondition the extractor applies — eliminating
~95% wasted enqueue/lease/deserialize cycles; the freed lane is where the
expiry sweep lives). Wire enums keep their values for serialization
compatibility; only dead *behavior* is deleted.

## Risks / Trade-offs

- [Model download unavailable offline at first run] → loud degraded mode:
  doctor Error, daemon status `embeddings: degraded`, rate-limited logs;
  lexical recall keeps serving. Never silent.
- [Query-embedding latency blows the 300 ms recall budget on CPU] →
  **confirmed with fixed-512 padding, then resolved by measurement** (Slice 2
  task 2.13: p95 ≈ 315 ms, ~2× over the 150 ms sub-budget on the reference
  box). The dynamic-sequence-length experiment (see D6 and Open Questions)
  confirmed the ONNX graph's sequence axis is symbolic (not a fixed shape)
  and measured short-query p95 at 20.9 ms once padding matches actual token
  length — ~7× under budget, with 1.000000 cosine parity against fixed-512
  across 10 test sentences. Slice 4 ships dynamic-length padding
  (bucket-of-8) as the mitigation; warmup inference at start and
  `RecallTimeoutMs` remain in place as defense-in-depth, not as the primary
  fix.
- [LLM merge synthesis loses information] → MergeGuard token-retention check
  + structural-append fallback; consolidation applies only via human-ratified
  plan files with a backup taken first.
- [Cosine floor calibrated on one corpus generalizes poorly] → floor lives in
  config next to `ModelId`; gold-set eval (real traffic) pins the calibration;
  doctor warns on mixed-model embedding rows.
- [Searchable-out-of-auto surprises users who relied on incidental recall] →
  BREAKING is called out; `find_memories` covers the tail; formation default
  changes only affect NEW memories; consolidation plans may propose
  recall-mode changes but only under ratification.
- [ARM64 native OnnxRuntime regression] → CI publish smoke leg on linux-arm64;
  FastBertTokenizer is pure managed.
- [Two write paths drift again during the transition] → shared
  `MemoryCurationEvaluator` lands as its own slice before any nominator work;
  divergence becomes structurally impossible rather than reviewed-for.

## Migration Plan

1. Slices are independently shippable, in order: (1) shared evaluator
   extraction (behavior-neutral refactor), (2) embedding foundation (writes
   vectors, nothing reads them — zero behavior risk), (3) write-side
   nominate→decide + lossless merge, (4) read-side hybrid + cosine floor,
   (5) taxonomy rebalance + trace revival + tool lessons, (6) maintenance
   CLI + expiry sweep + subtraction.
2. Existing corpora: `backfill-embeddings` (measured: minutes) is required
   before slices 3–4 activate their vector paths; both paths degrade loudly
   to lexical when coverage is incomplete rather than misbehaving.
3. Rollback: each slice is config-gated (`Memory.Embeddings.Enabled`,
   nominator/recall thresholds) — disabling returns to the quick-win
   behavior. Vectors are derived data; dropping `memory_embeddings` is safe.
4. Schema: new tables via idempotent `InitializeAsync` DDL; config surface
   added to `netclaw-config.v1.schema.json` with defaults (migration-friendly
   per the constitution's schema rules); `netclaw-memory` system skill updated
   in the same PR as each behavior slice.

## Open Questions

- ~~ONNX int8 query-embedding latency on reference hardware (measure in
  Slice 2; gates Slice 4's sub-budget design)~~ **MEASURED (Slice 2 task
  2.13, `tools/embed-latency-bench`, batch=1, 200 timed iterations/corpus
  after 20 warmups)**. Production path is fp32, not int8 (int8 remains a
  deferred D2 optimization). Reference box: i9-9900K, 8 logical cores,
  contended condition (load avg 2.0–3.6, ~11/15 GiB RAM in use, live daemon
  running):

  | corpus                      | tokens (mean) | p50     | p95    |
  |------------------------------|---------------|---------|--------|
  | short query                  | 13.8          | 281 ms  | 315 ms |
  | medium (~180 tok)             | 178.2         | 274 ms  | 298 ms |
  | doc-length (~440 tok)         | 442.1         | 275 ms  | 294 ms |
  | short, concurrency=2          | 13.8          | 274 ms  | 291 ms |
  | cold load (model load + 1st embed) | —        | 1069 ms | —      |

  All three corpora cost nearly the same regardless of length, because
  `OnnxMemoryEmbedder` always runs a fixed 512-token forward pass (no
  length-based truncation) — the fp32 matmul, not tokenization, dominates.
  Concurrency=2 gave no throughput benefit on this contended box (two
  parallel 100-call loops took as long in aggregate as one sequential
  200-call stream). **Verdict: the 150 ms query-embedding sub-budget does
  not hold on this hardware — p95 is ~2.1× over budget (margin ≈ −165 ms)**;
  the highest-leverage unexplored mitigation is a query-specific max-length
  (e.g. 64 tokens, not int8 quantization) before Slice 4 ships.
- ~~Does dynamic (query-specific) sequence length actually work on this ONNX
  graph, and is it a drop-in change?~~ **MEASURED AND RESOLVED** (same
  `tools/embed-latency-bench`, dynamic-length extension, same box, same
  batch=1/200-iteration/20-warmup protocol). Step 1: `InferenceSession
  .InputMetadata` shows all three inputs (`input_ids`, `attention_mask`,
  `token_type_ids`) declare shape `[batch_size, sequence_length]` — both
  dimensions symbolic, not fixed — so the graph accepts any sequence length;
  no re-export required. Step 2: padding each input to its actual tokenized
  length (rounded up to a multiple of 8) instead of fixed 512:

  | corpus                | tokens (mean) | fixed-512 p50 | fixed-512 p95 | dynamic-len p50 | dynamic-len p95 |
  |------------------------|---------------|---------------|---------------|------------------|------------------|
  | short query            | 13.8          | 281.9 ms      | 310.5 ms      | **19.0 ms**      | **20.9 ms**      |
  | medium (~178 tok)      | 178.2         | 281.7 ms      | 312.2 ms      | **84.1 ms**      | **93.3 ms**      |
  | doc-length (~442 tok)  | 442.1         | 280.3 ms      | 304.6 ms      | **235.5 ms**     | **250.1 ms**     |

  Step 3, correctness (not just speed): 10 fixed sentences (5 short queries +
  5 longer bank sentences), embedded both ways, cosine similarity fixed-512
  vs dynamic-length — **1.000000 on all 10 (min = mean = 1.000000)**: the
  attention mask fully accounts for the padding difference, so this is a
  correctness-neutral, pure-performance change. Contention context: load
  average 1.40/1.44/2.36 before the ~6-minute run, 4.76/3.63/3.08 after (the
  run's own CPU load, not external contention). **Verdict: dynamic sequence
  length is adopted as the Slice 4 mitigation** — short-query p95 lands at
  ~14% of the 150 ms sub-budget (huge margin), medium and doc-length both
  drop meaningfully too. Int8 quantization and relaxing the sub-budget are no
  longer necessary; both remain available as future levers if traffic shifts
  toward longer queries.
- Final `MinCosineSimilarity` default (calibrate against `gold-prod-2026-07`
  during Slice 4; 0.55 is the working hypothesis).
- Whether the R2 feeds channel should mirror model artifacts (post-PoC
  operational decision; allowlist design is unaffected).
- Trace auto-recall weighting while fresh (small prior vs durable-fact parity)
  — decide with eval cases in Slice 5.
- ~~Can int8 quantization become the default embedding model, and does its
  quality hold up against fp32 (Slice 4 Stage A)?~~ **MEASURED AND
  RESOLVED — NOT SHIPPED.** `tools/embed-latency-bench` `parity` mode
  measured `snowflake-arctic-embed-m-int8` (HuggingFace's `model_uint8.onnx`,
  QUInt8 dynamic quantization) against fp32 on a 40-sentence probe set +
  15 pairs (10 near-dup, 5 unrelated): mean per-sentence cosine parity 0.9829
  against a ≥0.99 gate (**fail**), driven entirely by doc-length content
  (0.953–0.981) while short queries stayed ≥0.99; max pair-cosine delta
  0.0231 against a ≤0.02 gate (**fail**, barely). The signed QInt8 sibling
  (`model_int8.onnx`) was also measured as a cross-check — same shortfall
  (mean parity 0.9807), so the failure is not specific to one 8-bit scheme.
  Near-dup/unrelated separation held under int8 in both cases, so
  `NominatorSimilarityThreshold=0.86` would not need recalibration if int8
  ever shipped — the blocker is purely the per-sentence/pair-delta parity
  gate. **Decision: fp32 stays the default; int8 remains an allowlisted
  opt-in, not a non-decision default.** Open follow-up: whether a
  statically-calibrated (not dynamic) quantization method closes the gap,
  deferred past Stage A.
- Remaining open question for the 1 GB pod goal: dynamic sequence length
  alone (fp32, shipped) got RSS from ~988 MB to ~636 MB — real progress, but
  636 MB + the ~397 MB daemon peak (≈1033 MB) is still marginally over a
  1 GB limit. Fully closing that gap needs either an operator-accepted int8
  opt-in or the deferred remote embedder mode (see Non-Goals).
