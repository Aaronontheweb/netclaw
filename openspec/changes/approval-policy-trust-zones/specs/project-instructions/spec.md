## REMOVED Requirements

### Requirement: Project identity file loading from project directory
**Reason:** Daemon-side auto-loading depends on
`WorkingContext.ProjectDirectory`, which is being deleted. The agent
reads project identity files on demand via `file_read` per the explicit
lookup discipline added to `Resources/AGENTS.md`. The same file order
(`.netclaw/AGENTS.md` → `AGENTS.md` → `CLAUDE.md` → `CONTEXT.md`)
applies, just driven by the agent rather than the daemon.
**Migration:** Delete the daemon-side identity-file-loading code path
(`SystemPromptAssembler` project layer, `FileSystemPromptProvider`
project handling, etc.). Update `Resources/AGENTS.md` to instruct the
agent: "When you start operating on files in a project, locate the
project root and read project context once via `file_read` in this
order: `.netclaw/AGENTS.md`, `AGENTS.md`, `CLAUDE.md`, `CONTEXT.md`.
Read the first one that exists. Don't re-read on later turns — the
content is in your conversation history."

### Requirement: Project instructions in system prompt
**Reason:** The system prompt no longer includes auto-loaded project
content because there is no `ProjectDirectory` to drive the load. The
agent's `file_read` of project identity files lands the content in
conversation history, not the system prompt — same caching behavior
within a single session, no auto re-injection on project switch
(because there are no project switches; switches happen by the agent
re-reading new project files).
**Migration:** Delete the project-instructions slot from
`SystemPromptAssembler.Assemble()`. Remove the `SetSystemPrompt()`
re-assembly trigger that fired on `set_working_directory` calls (the
trigger was tied to the deleted tool). System prompt becomes shorter
by ~6k tokens in observed sessions where project_dir had been set;
agent picks up the slack via on-demand reads.
