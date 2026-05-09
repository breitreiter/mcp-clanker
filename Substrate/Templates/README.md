# Project substrate

Structured project knowledge for this repo. Every entry has a kind
(rule, aspiration, learning, plan, task, reference), frontmatter,
and explicit drift semantics. Concept pages under `concepts/` are
auto-generated; everything else is human- or proposal-curated.

Full conventions: `_meta/conventions.md`.

## Layout

- `rules/` — locked-in constraints. Code violations = alarm.
- `aspirations/` — what we're going for. Internal contradictions are fine.
- `learnings/` — discovered knowledge. Decays in relevance, not in truth.
- `plans/active/` — primary working area. Most new work starts here.
- `plans/archive/` — concluded plans (shipped, shelved, abandoned).
- `tasks/` — task tracking.
- `concepts/<topic>.md` — auto-generated synthesis pages.
- `reference/` — external-system references.
- `log.md` — append-only chronological history.

## Trust model

- **You (human)** and **foreground Claude** — full read/write here.
- **imp (background)** — read-only. Produces proposals at
  `{{REPO}}.project-proposals/` for review and approval.

See `_meta/conventions.md` for the auto-approval gradient and the
proposal categories.
