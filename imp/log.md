# Substrate log

Append-only chronological record of substrate changes and gnome
sweep findings. Entries lead with `## [date] kind | title` so simple
grep works (`grep "^## \[" log.md | tail -20`).

---

## [2026-05-10] init | substrate created

Substrate initialized via `imp init`. Layout: gnome-maintained
`learnings/`, `reference/`, `concepts/`, `_index/`, `note/`,
`log.md` under `imp/`; human-owned `plans/`, `bugs/`, `TODO.md`,
`rules/` at repo root.
