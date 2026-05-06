## Context

Shell command approval patterns are extracted by `ShellTokenizer.ExtractVerbChain()`
which, for path-aware verbs (`ls`, `cat`, `grep`, `find`, etc.), appends the
first file-path argument to the verb chain. This produces per-file patterns
like `cat /home/.netclaw/logs/crash-foo.log`. Combined with the single-token
exact-match restriction in `ApprovalPatternMatching` (which prevents bare `cat`
from silently approving `cat /etc/shadow`), each unique file path requires a
separate interactive approval.

The approval system has three security layers:
1. Hard deny list (before approval gate)
2. Interactive approval gate (`ToolAccessPolicy` + `IToolApprovalService`)
3. `ToolPathPolicy` protected-path enforcement (at execution time, after approval)

This change only relaxes layer 2. Layers 1 and 3 are unaffected.

## Goals / Non-Goals

**Goals:**
- Reduce per-file approval fatigue for diagnostic shell commands
- Store directory-scoped patterns when user selects B (session) or C (always)
- Maintain boundary-safe path matching (no `StartsWith` — use `PathUtility.IsWithinRoot`)
- Prevent overly broad directory scopes (minimum 2 path segments)
- Show directory context in approval option labels only when the entire request
  maps cleanly to one directory scope

**Non-Goals:**
- Changing the hard deny list or `ToolPathPolicy` behavior
- Changing "Approve once" (A) behavior — it remains exact-pattern
- Glob-aware or regex-based pattern matching
- Cross-verb directory approvals (`cat /dir/` does not approve `grep /dir/`)
- Inferring indirect path flow through shell constructs like `xargs`, `eval`,
  loop variables, command substitution, or shell variables
- Windows-native shell path handling; tracked separately in issue #899

## Decisions

### Pattern convention: trailing `/` sentinel

Directory-scoped patterns use a trailing `/` to distinguish from exact patterns:
`grep /home/.netclaw/logs/` vs `grep /home/.netclaw/logs/daemon.log`.

**Why not a separate storage format?** The approval store (`tool-approvals.json`)
is a flat list of strings per tool per audience. A sentinel convention avoids
schema changes and keeps backward compatibility — existing non-slash patterns
work unchanged.

### Extraction: shared path-operand resolution for exact and directory patterns

`ShellTokenizer.TryExtractPathOperand()` is the shared primitive behind
`ExtractApprovalPattern()` and `ExtractDirectoryScope()`. It scans ALL non-flag
arguments for the first token that can be normalized into a path operand, using
the shell tool `WorkingDirectory` to resolve relative operands before approval
patterns are extracted or matched. This solves the grep problem where the search
term is the first positional arg and the file path is second
(`grep -l "timeout" logs/daemon.log`).

When a recognizable path operand exists, exact approval patterns use the
normalized path operand itself (`grep /abs/path/logs/daemon.log`), not the raw
verb chain. Directory-scoped patterns then derive scope from that same operand.

**Alternative considered:** Always use first positional. Rejected because grep,
sed, and awk take non-path first arguments.

### Existing directory operands stay directory-scoped

`ExtractScopedDirectory()` preserves an operand that already denotes a
directory. For example, `find logs -name '*.log'` resolves `logs` against the
working directory and stores `find /abs/path/logs/` rather than widening to the
parent (`/abs/path/`). This keeps approval scope aligned with what the command
actually targets.

### Matching: `PathUtility.IsWithinRoot()` with normalized operands

Directory matching delegates to `PathUtility.IsWithinRoot()` which normalizes
both paths, uses platform-appropriate case sensitivity, and checks boundary
characters. This prevents `/home/usersecret` from matching an approval for
`/home/user`.

Because both exact and directory extraction share normalized path operands,
relative requests such as `cat logs/app.log` are matched against approvals using
their resolved absolute path under the shell `WorkingDirectory`.

### Minimum depth: 2 segments below root

`CountPathSegments()` rejects scopes shallower than 2 segments (blocks `/`,
`/home/`, `/etc/`, `/tmp/`). This is a hard floor — the user cannot approve
at root-level directories even if they want to.

### Verb isolation

An approval for `cat /dir/` does NOT approve `grep /dir/`. The verb is part of
the pattern and checked explicitly. This limits blast radius — approving reads
doesn't silently approve writes or deletions.

### Dynamic labels require a single clean directory scope

`ToolAccessPolicy.TryGetSingleDirectoryScope()` only emits directory-specific B/C
labels when every approval pattern for the request is directory-scoped and all
of them resolve to the same directory. If any segment falls back to a generic
verb-chain pattern (for example `git push`) or multiple directory scopes are
present, labels stay generic.

### Compound commands: direct path operands only

Compound commands and pipe segments are still traversed segment-by-segment, so a
segment like `cat logs/app.log | jq .` can contribute directory scope for the
`cat` segment. MVP extraction stops there: it does not infer that a downstream
segment implicitly targets the same path through `xargs`, `eval`, loop
variables, shell variables, or similar constructs.

## Risks / Trade-offs

**[Risk] Directory scope is broader than per-file** → Mitigated by
`ToolPathPolicy.CommandReferencesDeniedPath()` at execution time, which
independently blocks access to protected files (`config/netclaw.json`, keys,
`secrets.json`) regardless of approval state.

**[Risk] Compound commands often cannot use directory-specific labels** →
Mixed approval sets (directory patterns plus verb-chain fallbacks, or multiple
directories) keep the generic labels. This is intentional; a generic label is
less misleading than showing a partial directory scope for the whole request.

**[Risk] Minimum depth too restrictive** → A 2-segment minimum blocks
`/etc/` and `/tmp/` which are legitimate diagnostic targets. This is intentional
— those directories contain sensitive system files. Users can still approve
individual files via "Approve once".
