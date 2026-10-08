# PARKED: skill guidance position, netclaw-memory split, size gate

Parked on 2026-10-08 by owner decision. This branch has no pull request.
Draft pull request https://github.com/netclaw-dev/netclaw/pull/2418 was opened
before the decision and is closed. Delete this file before a pull request.

## What is on this branch (all committed)

- Eval category "Skill Guidance Position" in `evals/run-evals.sh` (13 cases),
  `evals/skill_position_evals.py`, `evals/test_skill_position_evals.py`,
  `evals/fixtures/skill-position/facts.json`, README section, CI line.
- `evals/skill_visible_windows.py`: the part of each system skill file that
  the model reads (budget from `SessionTuning.cs`).
- `netclaw-memory` split: `SKILL.md` (16,017 -> 6,221 result characters),
  `references/recall-internals.md` (8,627), `references/diagnostics.md` (4,955),
  version 1.15.0.
- `SystemSkillSizeGateTests` with four exemptions.
- Base of the branch: upstream `dev` at b8f5cfaa4.

## Two accuracy defects in the shipped netclaw-memory skill

They are fixed apart from this branch in
https://github.com/netclaw-dev/netclaw/pull/2419. When this work resumes,
rebase over that fix and drop the same corrections here.

- `feeds/skills/.system/files/netclaw-memory/SKILL.md` on `dev`, lines 289-290:
  "`Memory.Embeddings.Enabled` is `true` (default `false` for now)". The code
  default is `true` (`MemoryEmbeddingsConfig.Enabled` in
  `src/Netclaw.Configuration/MemoryConfig.cs`, and the schema).
- Same file, lines 283-285: "Formation pipeline (grep for `memory_observation`)",
  `memory_observation_sidecar_completed`, `memory_observation_gate_result`. The
  code has no such event. The real events are `session_observer_*`
  (`SessionMemoryObserverActor`) and `memory_curation_completed|skipped|failed`
  (`LlmSessionActor`).

## Base table (complete)

Endpoint https://llm.testlab.petabridge.net, model
`/models/Qwen3.8-27B-UD-Q4_K_XL.gguf`, daemon from `dev` at c4995e9b4 with
unchanged skills and without #2414, threshold 0.80. Runs: 5 for head, tail,
and middle_oauth_redirect_steer; 3 for the others.

| Case | Pass | Facts correct, any route | Tool calls | Skill text chars | Steer shown | Runs with tool_output_read | Runs with a physical skill read | Failure reasons |
|---|---|---|---|---|---|---|---|---|
| head | 5/5 | 5/5 | 1.2 | 14,459 | 0/5 | 0 | 0/5 | - |
| tail | 5/5 | 5/5 | 1.0 | 12,049 | 0/5 | 0 | 0/5 | - |
| middle_oauth_redirect | 0/3 | 3/3 | 5.3 | 25,496 | 0/3 | 0 | 3/3 | physical read 3 |
| middle_approvals_quarantine | 0/3 | 3/3 | 6.7 | 37,805 | 0/3 | 0 | 3/3 | physical read 3 |
| middle_long_commit (behavior) | 0/3 | 0/3 | 5.3 | 14,584 | 0/3 | 0 | 0/3 | long text inline 3 |
| reference_middle | 0/3 | 2/3 | 7.7 | 28,202 | 0/3 | 0 | 3/3 | physical read 2, fact absent 1 |
| middle_oauth_redirect_steer | 3/5 | 3/5 | 2.6 | 15,287 | 3/5 | 3/5 | 0/5 | timeout 2 (240 s limit) |
| reference_middle_steer | 2/3 | 3/3 | 5.7 | 36,877 | 3/3 | 3/3 | 1/3 | physical read 1 |
| right_reference_operations | 3/3 | 3/3 | 2.0 | 20,078 | - | 0 | 0/3 | - |
| memory_no_recall_block | 3/3 | 3/3 | 1.0 | 12,049 | 0/3 | 0 | 0/3 | - |
| memory_tool_choice | 3/3 | 3/3 | 11.3 | 20,082 | 0/3 | 0 | 0/3 | - |
| right_reference_memory | not applicable on base | | | | | | | |

## After-run and second-model numbers (PARTIAL)

Branch image = c4995e9b4 plus the memory split. Same endpoint and model as the
base table, 3 runs:

| Case | Base | Branch | Skill text chars (base -> branch) |
|---|---|---|---|
| memory_no_recall_block | 3/3 | 3/3 | 12,049 -> 6,220 |
| memory_tool_choice | 3/3 | 2/3 | 20,082 -> 6,220 |
| right_reference_memory | n/a | 3/3 | 14,846 (skill 6,220 + reference 8,626) |

The one failed `memory_tool_choice` run was an assertion defect, not a
regression: an earlier run of the same daemon saved the subject, and the agent
used `update_memory` for the save turn. Commit 8683421d0 corrects the
assertion. The corrected case did not run again.

Existing categories on the branch, one run for each case, same endpoint and
model: "Memory Pipeline" 5 of 5, "Skill Discovery" 9 of 9. No recheck on base
was necessary.

Second model, PARTIAL: endpoint https://spark2.testlab.petabridge.net, model
`deepseek-v4-flash-vision-exp`, base image, 5 runs:

- memory_no_recall_block: 5/5
- middle_long_commit: 0/5 (the same failure as Qwen: long text inline)
- No other case finished. No branch run on this model.

## Not done

- Five-run reruns of the memory cases and `middle_long_commit` (stopped).
- After-run of the `netclaw-operations` cases (the memory split cannot change them).
- Second-model runs of the memory cases on the branch.
- The `netclaw-operations` split. The plan (index of about 8,000 characters
  and 17 references) is in the body of the closed pull request #2418.

## How to resume

1. Rebase onto `dev`. #2414 changes `netclaw-operations/SKILL.md` ("Large tool
   output"), so run `python3 -m unittest discover -s evals -p 'test_*evals.py'`
   and correct `facts.json` if a fact moved. Run `SystemSkillSizeGateTests` and
   correct the exemption limits. Drop the two corrections that #2419 has.
2. After #2414, each session gets the continuation line. Read the README
   section again: the two "steer" cases then equal their single-turn cases.
3. Build two images (`IMAGE_REPO=netclaw-eval scripts/docker/build-image.sh <tag>`),
   one from the base commit and one from the branch. Run each case with
   `NETCLAW_EVAL_NO_BUILD=1 NETCLAW_IMAGE=<tag> NETCLAW_BIN=<cli> NETCLAW_EVAL_CASE=<case>`.
   Use one endpoint and one model for the base run and the branch run of a case.
4. The local images and run archives of this work were deleted.
