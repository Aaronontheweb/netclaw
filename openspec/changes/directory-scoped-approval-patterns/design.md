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
- Show directory context in approval option labels

**Non-Goals:**
- Changing the hard deny list or `ToolPathPolicy` behavior
- Changing "Approve once" (A) behavior — it remains exact-pattern
- Glob-aware or regex-based pattern matching
- Cross-verb directory approvals (`cat /dir/` does not approve `grep /dir/`)

## Decisions

### Pattern convention: trailing `/` sentinel

Directory-scoped patterns use a trailing `/` to distinguish from exact patterns:
`grep /home/.netclaw/logs/` vs `grep /home/.netclaw/logs/daemon.log`.

**Why not a separate storage format?** The approval store (`tool-approvals.json`)
is a flat list of strings per tool per audience. A sentinel convention avoids
schema changes and keeps backward compatibility — existing non-slash patterns
work unchanged.

### Extraction: scan all arguments, not just first positional

`ShellTokenizer.ExtractDirectoryScope()` scans ALL non-flag arguments for the
first `LooksLikePath()` token, then extracts its parent directory. This solves
the grep problem where the search term is the first positional arg and the file
path is second (`grep -l "timeout" /home/.netclaw/logs/daemon.log`).

**Alternative considered:** Always use first positional. Rejected because grep,
sed, and awk take non-path first arguments.

### Matching: `PathUtility.IsWithinRoot()` not `StartsWith`

Directory matching delegates to `PathUtility.IsWithinRoot()` which normalizes
both paths, uses platform-appropriate case sensitivity, and checks boundary
characters. This prevents `/home/usersecret` from matching an approval for
`/home/user`.

### Minimum depth: 2 segments below root

`CountPathSegments()` rejects scopes shallower than 2 segments (blocks `/`,
`/home/`, `/etc/`, `/tmp/`). This is a hard floor — the user cannot approve
at root-level directories even if they want to.

### Verb isolation

An approval for `cat /dir/` does NOT approve `grep /dir/`. The verb is part of
the pattern and checked explicitly. This limits blast radius — approving reads
doesn't silently approve writes or deletions.

## Risks / Trade-offs

**[Risk] Directory scope is broader than per-file** → Mitigated by
`ToolPathPolicy.CommandReferencesDeniedPath()` at execution time, which
independently blocks access to protected files (`config/netclaw.json`, keys,
`secrets.json`) regardless of approval state.

**[Risk] `directoryPatterns[0]` drives UI label for compound commands** →
For compound commands with multiple path-aware segments targeting different
directories, only the first directory appears in the label. Acceptable because
compound commands targeting multiple directories are rare, and the approval
still covers the right patterns.

**[Risk] Minimum depth too restrictive** → A 2-segment minimum blocks
`/etc/` and `/tmp/` which are legitimate diagnostic targets. This is intentional
— those directories contain sensitive system files. Users can still approve
individual files via "Approve once".
