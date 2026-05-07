## Context

The current shell approval flow reuses exact command patterns too narrowly for
diagnostic work. In session `D0AC6CKBK5K/1778163885.517639`, repeated shell work
inside the same directories still produced repeated prompts because each new
file path became a new approval target. Issue `#905` is only contextual here:
it explains why that one session had so many shell calls, but this change does
not attempt to solve the broader issue volume.

This update shifts broader shell approvals away from command classification and
toward reusable local directory roots. The approval question becomes: can this
shell approval unit be shown to stay under roots the user already approved?

When the answer is yes, later shell commands under those roots should not ask
again. When the answer is no, the system must fall back to exact approval
behavior.

The approval system has three security layers:
1. Hard deny list (before approval gate)
2. Interactive approval gate (`ToolAccessPolicy` + `IToolApprovalService`)
3. `ToolPathPolicy` protected-path enforcement (at execution time, after approval)

This change only relaxes layer 2. Layers 1 and 3 are unaffected.

## Goals / Non-Goals

**Goals:**
- Reduce repeated shell approval fatigue for later commands under the same local
  directories
- Keep `Approve once` exact blocked-call retry only
- Store reusable directory roots for shell B/C approvals when local roots are
  extractable
- Auto-approve later shell approval units only when all recognized local paths
  stay under already approved roots
- Keep boundary-safe root matching and minimum-depth enforcement
- Show root context in approval option labels

**Non-Goals:**
- Changing the hard deny list or `ToolPathPolicy` behavior
- Changing `Approve once` (A) behavior
- Classifying shell safety by command verb families
- Using directory-root approvals when no reusable local roots can be extracted
- Glob-aware or regex-based approval matching

## Decisions

### Approval units: split on shell control operators, not pipelines

The broader approval model operates on shell approval units instead of whole
commands or individual tokens.

- `&&`, `||`, and `;` start a new approval unit
- `|` stays inside the current approval unit

This preserves the user's expectation that a pipeline like
`grep ... /home/.netclaw/logs/app.log | wc -l` is one piece of work, while still
preventing a later `&& rm ...` segment from inheriting that approval.

### Directory roots replace verb-scoped directory patterns

For `shell_execute`, B and C approvals store reusable local directory roots, not
verb-specific patterns. A later `ls`, `cat`, or `grep` can reuse the same root
approval as long as every recognized local filesystem path in that approval unit
resolves under approved roots.

This is intentionally verb-agnostic. The safety boundary moves from shell verb
classification to filesystem containment plus the existing backstops.

### Extraction: recognized local filesystem paths across the whole unit

Root extraction scans each approval unit for recognized local filesystem paths,
not just the first positional token. That covers forms like
`grep -l "timeout" /home/.netclaw/logs/daemon.log`, multiple path arguments, and
paths inside a pipeline.

If one or more local paths are found, the system derives directory roots from
them. If none are found, directory-root approval is unavailable and the system
falls back to exact approval behavior for that unit.

### Matching: all recognized local paths must stay under approved roots

Auto-approval succeeds only when every recognized local filesystem path in the
candidate approval unit resolves under an already approved root.

This avoids partial matches where one safe path could accidentally approve a
unit that also touches another directory the user never approved.

### Root comparison remains boundary-safe

Root matching delegates to `PathUtility.IsWithinRoot()`, which normalizes paths,
applies platform-appropriate case sensitivity, and checks boundaries. This keeps
`/home/usersecret` from matching a root approval for `/home/user`.

### Minimum depth: 2 segments below root

Derived roots shallower than 2 segments are rejected. That still blocks broad
roots such as `/`, `/etc/`, and `/tmp/` from becoming reusable directory
approvals. Those commands can still proceed through exact approval behavior.

### Tiny internal representation: display path vs comparison root

Internally, each extracted directory root should carry a tiny pair:

- a display path for approval labels
- a normalized comparison root used for containment checks

This keeps the behavior model focused on roots while avoiding UI drift from the
path form used for comparisons.

## Risks / Trade-offs

**[Risk] Root approvals are broader than per-file approvals** → Mitigated by
minimum depth enforcement, normalization, traversal checks, and
`ToolPathPolicy.CommandReferencesDeniedPath()` at execution time.

**[Risk] Multi-path approval units can be harder to explain in the prompt** →
The prompt can display the primary extracted roots, but matching still requires
all recognized local paths in the unit to stay under approved roots.

**[Risk] Some shell commands have no reusable local roots** → This is expected.
When no local roots are extractable, the system falls back to exact approval
behavior instead of inventing a broader approval class.
