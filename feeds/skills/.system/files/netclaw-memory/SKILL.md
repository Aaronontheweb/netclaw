---
name: netclaw-memory
description: "REQUIRED when the user asks what you remember, recall, or know from past conversations, previous sessions, cross-session memory, memory classes, or memory types. Also before using memory tools: find_memories, get_memories, store_memory, update_memory."
metadata:
  author: netclaw
  version: "1.15.0"
---

# Netclaw Memory

Read this before you use a memory tool. It tells you how memory works and
when to use each tool.

## References

This file holds the rules for your behavior. Operator and developer detail is
in two reference files. Read one only when the task needs it.

| Read this reference | When |
|---------------------|------|
| `skill_read_resource('netclaw-memory', 'references/recall-internals.md')` | The user asks how recall selects or ranks memories: hybrid recall, score weights, the cosine floor and its value, the relevance gate, the embedding models, a backfill or a model change, duplicate detection. |
| `skill_read_resource('netclaw-memory', 'references/diagnostics.md')` | Memory behavior looks wrong, or the user asks for memory log events, the embeddings status, the doctor checks for memory, or the memory evals. |

## Audience and Feature Gating

Memory has two independent gates:

- **Audience gate:** A Public session has no memory tools, no automatic
  recall, and no memory extraction. Memory does nothing for Public: no reads,
  no writes, no recall. Recall and search also exclude, for every audience,
  the historical memories that a Public session wrote.
- **Deployment gate:** `Memory.Enabled` in `netclaw.json` (default `true`).
  When it is `false`, memory is off for ALL audiences: recall returns nothing,
  tool discovery hides the memory tools, and the observation sidecar does no
  extraction.

Memory works only when both gates pass.

## How Memory Works

- **Automatic recall** runs before each user turn. It puts relevant
  `durable_fact` memories, and sometimes `evidence` memories, into the
  conversation.
- Recall is **selective by design**. A candidate must pass a relevance floor
  and fit a character budget for the turn, so **many turns get no memory at
  all**. When a turn has no `[memory-recall]` block, nothing relevant passed
  the floor. This is the normal, healthy result for most turns. It is not a
  malfunction, and it is not evidence that memory is broken. Never tell the
  user "my memory isn't working" because a turn had no `[memory-recall]`
  block. Use `find_memories` when you think that relevant memories exist and
  automatic recall did not show them.
- Recall is **policy-aware**: `audience` and `boundary` control what recall
  can show for the current turn.
- Recall resolves one time at the start of the turn. The tool loop of that
  turn uses the same result.
- A recalled memory can stay in the session history as context. Thus the
  policy for a turn is **first-contact gating**. It does not remove, at a
  later time, information that an earlier turn of the session showed.
- **Explicit tools** give you manual control on top of automatic recall.
- Memory uses SQLite. It is cross-session only inside the active
  domain/boundary policy envelope.
- Automatic recall, `find_memories`, and `get_memories` show memory IDs
  (for example `doc-…` / `rec-…`). An ID is a stable, opaque handle. Copy it
  **verbatim** into `get_memories` or `update_memory`. Do not rewrite it or
  change its format.

## When to Use Explicit Tools

### `find_memories` + `get_memories`

Use them when:
- The user explicitly asks what you remember
- Automatic recall does not give enough for the question
- You need a targeted search that goes past the recalled items

Pattern: `find_memories("query")` -> read the results -> `get_memories("id1,id2")`

Normal `find_memories` behavior:
- It searches `durable_fact` and current `evidence`
- It excludes `trace`
- It hides expired evidence by default
- It obeys the effective `audience` and `boundary` of the current turn

### `store_memory`

Use it only for a deliberate save request:
- The user explicitly says "remember this" or "save this for later"
- You pin a high-value fact, decision, or preference

Do NOT call `store_memory` by reflex on routine turns. The observation
sidecar forms memories in the background automatically.

Policy rules for explicit writes:
- An explicit write inherits the `audience` and `boundary` of the current turn
- An explicit write can narrow the policy scope, but must never widen it
- Raw secrets, credentials, tokens, and private keys are never durable memory

### `update_memory`

Use it only to correct or supersede an existing memory.

Use the memory ID exactly as automatic recall, `find_memories`, or
`get_memories` shows it. For a document, prefer `new_content` when you replace
a full hydrated memory. Use `old_text` + `new_text` only for a precise
find-and-replace edit. To delete a memory, pass `delete: true`.

## Memory Classes

| Class | Recall | Expiry |
|-------|--------|--------|
| `durable_fact` | Auto-recall when it clears the relevance floor | Never expires |
| `evidence` | Search (`find_memories`); auto-recall only on very strong matches | Expires after 30 days |
| `trace` | Not searchable | Expires after 72 hours |

## Policy Envelope

Each durable memory item has these fields:

- `memory_class`
- `audience`
- `boundary`
- `domain`
- `sensitivity`
- `recall_mode`

The policy applies at write time and at read time. A correct class alone is
not sufficient: recall and an intentional search must also obey the active
trust context.

## Identity vs Memory

Identity files (`SOUL.md`, `AGENTS.md`, `TOOLING.md`) define **the agent**:
persona, tone, operating rules, and the user grounding from init (name,
timezone). Do **not** put project facts, research, tool findings, or
**durable facts and preferences about the user** (favorites, family, history,
working preferences) in identity files. Those go through the **memory
pipeline** (`store_memory`), and recall shows them when they are relevant. A
request from the user to "remember" a preference is a memory write, not a
`SOUL.md` edit.

If you are not sure, load `netclaw-operations` for the identity-vs-memory
triage guide.

## When Memory Behavior Looks Wrong

A turn with no `[memory-recall]` block is not such a case. For a real
problem, run `netclaw status` and `netclaw doctor`, then read
`references/diagnostics.md`.
