# TUI-002: Chat Visual Grammar

Source PRDs: `PRD-004`, `PRD-009`

## Design Intent

Netclaw chat uses a quiet console grammar.
The design gives content priority over application chrome.
Color, whitespace, weight, and cell background create the hierarchy.
Decorative glyph borders do not create the hierarchy.

This grammar has four goals:

- Make each turn easy to scan.
- Keep parallel work easy to follow.
- Keep approval context visible and calm.
- Keep native terminal selection clean.

## Selection Rule

The settled transcript contains no corner, border, rail, or divider glyphs.
The live region also avoids these glyphs where practical.
Cell background can create a visual surface because terminal selection does not copy its color.

Visible text must carry useful meaning when a user selects it.
Labels such as `Tool`, `Agent`, `Approval`, and `Failed` are semantic content.
Spinners, tree rails, corner glyphs, and ornamental rules are not semantic content.

## Visual Tokens

The application maps these roles to the active terminal palette.
The application does not require one fixed theme.

| Role | Purpose |
|------|---------|
| Canvas | The normal terminal background |
| Surface | A quiet cell background for a prompt or activity group |
| Surface strong | The selected event or the Composer |
| Primary | Netclaw identity and active controls |
| Human | User identity and user prompts |
| Success | Completed work |
| Warning | Thoughts and approval context |
| Danger | Failures and denied actions |
| Muted | Time, metadata, and inactive controls |

The design uses bold text only for identity, state, and a selected action.
The design uses dim text for metadata and shortcut hints.

## Spacing Rhythm

- The viewport uses a two-cell left margin at 60 columns or more.
- A turn uses one blank line before each speaker.
- A speaker label and message share a compact group.
- An activity group uses one blank line before and after the group.
- A completed tool group collapses to one summary row.
- The Composer uses two text rows plus one hint row.

## Region Grammar

### Session Bar

The Session Bar uses one row.
It shows the product, session, model, context, and connection state.
It uses a cell background or underline style without divider glyphs.

### Turn

The user prompt uses a quiet surface background.
The Netclaw response uses the canvas background.
The user label and time appear above the prompt.
The Netclaw label and time appear above the response.

### Activity Group

An active group uses one quiet surface background.
Each row uses a semantic type label and a state word.
Indentation shows ownership without tree-rail glyphs.
Parallel rows keep a stable order.

A settled group collapses to one receipt row:

`Tools  3 complete · 2.8s · Ctrl+O for details`

### Thought

An active thought uses one short warning-colored line.
A settled thought collapses to a token and duration receipt.
Raw thought text remains available in the Inspector when policy allows it.

### Approval

An approval uses an amber cell background at the top of the Decision Gate.
The header names the requester and the requested action.
The gate has no border glyphs.
The command uses a separate code surface.
Each decision uses a text label with an inverse selected state.

### Composer

The Composer uses a strong surface background and no border glyphs.
The first cell uses a primary background as a focus cue.
The prompt text starts after that cell.
The hint row lists only controls that work in the current terminal.

### Inspector

The Inspector owns the visible viewport while it is open.
The transcript returns when the user closes the Inspector.
The Inspector uses two background surfaces at wide widths.
The left surface lists semantic events.
The right surface shows complete safe detail.
A blank background gutter separates both surfaces.
The Inspector uses no vertical divider glyph.

## Content Rules

- Do not show raw JSON in the main transcript.
- Do not put a complete command on a status row.
- Do not join a tool result to the next agent event.
- Do not repeat a tool name on every progress update.
- Do not show an empty fixed-height transcript panel.
- Do not use uppercase for complete sentences.
- Do not use more than three emphasis colors in one region.
- Do not truncate a security decision target.

## Mockup Set

- `mockups/chat-quiet-normal.svg`
- `mockups/chat-quiet-active.svg`
- `mockups/chat-quiet-approval.svg`
- `mockups/chat-quiet-inspector.svg`

These files show the intended hierarchy at 120 columns.
They are design targets, not raster product screenshots.
