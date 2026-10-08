# Memory Diagnostics

This reference is for operators and developers. Use it when memory behavior
looks wrong. A turn with no `[memory-recall]` block is normal and is not a
reason to use this reference.

## First Steps

1. Run `netclaw status`.
2. Run `netclaw doctor`.
3. Load `netclaw-operations` for daemon and log guidance.
4. Read the operator runbook:
   <https://github.com/netclaw-dev/netclaw/blob/dev/docs/runbooks/memory-health-and-evals.md>.
   In a source checkout, the file is `docs/runbooks/memory-health-and-evals.md`.
   It has the checkpoint health checks, the relevance gate health checks, and
   the eval commands. This reference does not copy them.

## Log Events

The recall events and the observer events are in the daemon log. The
`turn_memory_recall` event and the `memory_curation_*` events are in the
session log (`log_path` in the `[session]` block).

**Recall pipeline** (search for `memory_retrieval` and `memory_recall`):

- `memory_retrieval_request_plan`: query tokens, facets, soft scopes, anchor
  hints.
- `memory_retrieval_candidate_selection`: all candidates with their selector
  scores.
- `memory_retrieval_final`: the results of the floor filter and the final
  items. It has `appliedFloor` and `floorSource` (`manifest` or `override`),
  so you can diagnose a floor mismatch without a read of the config. When the
  relevance gate ran, it also has `gateScores` (the cross-encoder score of
  each candidate that the gate scored) and `droppedByGate` (the number that
  the gate dropped).
- `turn_memory_recall`: the summary event, with the item count and the
  duration.
- `memory_recall_vector_degraded`: the turn used lexical-only recall. The
  embedder was unavailable, there was no vector index, or the query-embedding
  sub-budget was exceeded.
- `memory_recall_coverage_gap`: one or more candidates had no embedding row
  for the current model. They get a lexical score and are not excluded. The
  gap repairs itself through embed-on-write and
  `netclaw memory backfill-embeddings`.
- `memory_recall_gate_degraded`: recall skipped the relevance gate for this
  turn. The model was unavailable, the sub-budget was exceeded, or recall ran
  in lexical mode. Recall used the result of the floor without a filter.

**Formation pipeline** (search for `session_observer` and `memory_curation`):

- `session_observer_turn_trigger`: the observer starts a distillation after
  the configured number of turns.
- `session_observer_distill_skipped`: no distillation ran. The event gives the
  reason, for example `no_new_content`.
- `session_observer_accepted_proposals_persisted`: the number of memory
  proposals that the observer accepted and saved.
- `session_observer_parse_failed` and `session_observer_parse_no_json`: the
  observer could not read the output of the model.
- `memory_curation_completed`: the curator result, with the `evaluated`,
  `skipped`, `updated`, `consolidated`, and `created` counts.
- `memory_curation_skipped` and `memory_curation_failed`: the curation did not
  run, or it failed. The event gives the reason.

## Embeddings

When `Memory.Embeddings.Enabled` is `true` (the default), the daemon provisions
the embedding model at startup. When the model is unavailable:

- The log has `memory_embedding_unavailable` (the embedder) or
  `memory_relevance_gate_unavailable` (the relevance/cross-encoder model).
- The daemon status shows `embeddings: degraded`. The other values are `ok`
  and `disabled`.
- Lexical recall continues to work normally.
- An operator alert fires one time for each model in each daemon run:
  `memory.embedding_model.unavailable` or `memory.relevance_model.unavailable`.
  It uses the same notification sink as `provider.unreachable` and
  `reminder.execution.failed`. It names the model, the failure reason, and the
  consequence (lexical-only recall and dedup, or a relevance gate with no
  filter). This is the push signal. `netclaw doctor` and `netclaw status` are
  the pull signals.

The Memory Embeddings check of `netclaw doctor` reports whether the active
model has a query prefix (`queryPrefix=True/False`). It also reports the
effective retrieval floor and its source (`floor=0.240 (source=manifest)`), or
`floor=none ...` when the active model has no retrieval calibration and no
override is configured. Read this check first when recall quality looks wrong
after a model change or a config change.

To fill the memory vectors again after you enable embeddings:

```
netclaw memory backfill-embeddings [--force]
```

The command does nothing while `Memory.Embeddings.Enabled` is `false`.

See `references/recall-internals.md` for the floor, the gate, and the model
change behavior.

## Eval Gate

Before a rollout, run the redesigned provider-independent eval suites first.
Then you can run live smoke checks with local Ollama models. The runbook has
the commands.
