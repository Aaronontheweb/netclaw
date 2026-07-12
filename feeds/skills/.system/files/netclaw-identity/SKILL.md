---
name: netclaw-identity
description: "REQUIRED when the user asks to define or change the agent's mission, personality, communication style, operating playbook, recurring workflow, skill-selection rules, delegation practices, or identity files such as SOUL.md, AGENTS.md, or TOOLING.md."
metadata:
  author: netclaw
  version: "1.0.0"
---

# Netclaw Identity and Mission

Use this skill before changing Netclaw identity files. Each file has one owner:

| Content | File |
|---|---|
| Agent personality, tone, operator identity, communication style | `SOUL.md` |
| Deployment mission, recurring workflows, skill selection, delegation, review gates | `AGENTS.md` |
| Available environment capabilities and tool configuration | `TOOLING.md` |

Durable facts and preferences that do not define the agent belong in memory.
Project-specific instructions belong in the project's identity file, not the
deployment playbook.

## Authoring a Deployment Playbook

1. Ask what function the deployment performs, for whom, and what success means.
2. Identify recurring tasks, known failure modes, required skills, delegation
   boundaries, and review steps.
3. Propose a concise playbook and obtain operator confirmation.
4. Read the current `AGENTS.md` before editing it. Preserve useful content and
   its section structure.
5. Write durable instructions that explain when and how to act. Do not copy
   volatile customer, deal, project, or runtime data into identity files.
6. Report what changed and that it applies on the next inbound message.

## Safety Boundaries

The deployment playbook is supplied to Personal, Team, and Public conversations
and inherited by sub-agents. Never place secrets, credentials, private customer
data, or audience-restricted facts in it. It augments Netclaw's embedded rules
and cannot disable ACL, approval, or tool-policy enforcement.

Identity-file tools do not authorize edits to `netclaw.json`, `secrets.json`,
ACL, or security policy. Direct the operator to the CLI or configuration editor
for those changes.
