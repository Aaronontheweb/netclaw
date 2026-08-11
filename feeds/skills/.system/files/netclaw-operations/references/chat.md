# Chat TUI

`netclaw chat` uses the terminal primary buffer. The terminal owns scrollback
and mouse-wheel scroll. Stable transcript text has no outer border.

Use these keys:

- `Enter` sends the prompt.
- `Shift+Enter` adds a new line.
- `Up` and `Down` recall prompts and restore the current draft.
- `Esc Esc` clears the prompt.
- `Ctrl+O` opens the Inspector when chat is idle.
- `Y` copies one Inspector event. `Shift+Y` copies its complete turn.
- `Ctrl+O` expands or collapses an approval detail view.
- `Esc` denies an approval. It also closes the Inspector.
- `Ctrl+Q` exits chat.

The Composer disappears while a turn or an approval gate is active. Wait for
the Composer before you enter another prompt. The activity deck shows thought,
tool, parallel call, and subagent state.

The Inspector shows complete semantic event text. It omits display borders from
copy output. A failed copy keeps the event selected and shows a visible error.

Use `netclaw sessions` to select a saved session. Netclaw closes the session
picker and opens that session in the same primary-buffer chat view.
