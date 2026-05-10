---
state: shipped
created: 2026-05-10
updated: 2026-05-10
shipped: 2026-05-10
---

# project-migrate Phase 1 — `imp migrate`

Build the deterministic phase of `/project-migrate` as a standalone
`imp migrate` command. No model calls. Produces a migration plan
that a future Phase 2 (cloud classification, lands later) consumes.

Source spec: `project/project-migrate-spec.md`. This plan narrows
that spec to what's safe to build now without committing to
embeddings or cloud subagents.

## Why this slice

Phase 1 is fully deterministic — file walks, git history, regex,
heuristic shape-sniffing. That maps cleanly onto the static-vs-
synthesis boundary (`imp/learnings/static-vs-synthesis-boundary.md`):
deterministic ops belong in `imp`, synthesis belongs in skills.

Phase 1 also has a built-in dogfood target: imp's own `project/` dir
holds ~18 design docs that pre-date the substrate, including several
just-deprecated wiki docs that should classify as supersede /
plans-archive. A useful end-to-end test is right there.

Phase 1.5 (embeddings) and Phase 2 (cloud classification) are
deferred — they need separate design moments and add cost the dogfood
doesn't need yet.

## Scope

**Builds:**
- `imp migrate [paths...]` command. Default-discover sources when no
  paths given.
- Auto-discovery: `project/` (skip if it contains
  `_meta/conventions.md`), top-level `README.md`, `CLAUDE.md` (the
  non-substrate section), other top-level `*.md` (TODO already
  excluded by being TODO.md and substrate-tracked).
- Per-doc signal gathering — delegate to existing
  `Substrate/Signals.cs`. No code duplication.
- Heuristic doc-shape sniff: `plan-shaped` | `mixed` | `flavor` |
  `reference` | `task-list` | `unknown`. Pure structural rules; no
  models. Spec acknowledges these will misfire and Phase 2 re-checks.
- Migration run dir: `<repo>.imp-proposals/_migration/M-NNN-<date>/`
  with `plan.md` (human-readable) + `plan.json` (machine-readable
  for Phase 2 consumption).
- Flags: `--include <glob>`, `--exclude <glob>`, `--out <dir>` (for
  testing without writing to the canonical location).

**Defers:**
- Phase 1.5 embeddings / clustering. Separate design moment when a
  second consumer materializes (per the spec's "shared `imp embed`"
  note).
- Cost estimator. No model calls yet → no cost to estimate. Add
  when Phase 2 lands.
- Phase 2 classification, proposal drafting, `--resume`,
  `--one-at-a-time`, cleanup proposal. All depend on Phase 2.
- The `/project-migrate` skill. Phase 1 is usable standalone; skill
  layer waits until Phase 2's synthesis work needs orchestration.

## Sniff heuristic (first cut)

Pure-structural rules over signals. Will misfire — Phase 2's job to
re-check. Order matters: first match wins.

1. **`task-list`** — primary content (>50% of non-blank lines) is
   `- [ ]` / `- [x]` / numbered checklist items.
2. **`reference`** — title or H1 contains "API"/"reference"/"docs",
   or content is heavy on external URLs (>1 URL per 20 lines) and
   light on code-symbol references (Signals.code_refs near zero).
3. **`plan-shaped`** — single H1 plus coherent H2 structure (≤8
   H2s), title contains "plan"/"spec"/"design"/"proposal", or
   frontmatter has `state:`. Single dominant topic by signal.
4. **`mixed`** — many H2s (>8) or H2 titles span clearly different
   kinds (e.g., "Rules" + "Decisions" + "Open questions" + "TODO").
5. **`flavor`** — high prose:citation ratio, no code-symbol
   references, narrative tense. Lore/worldbuilding-shaped.
6. **`unknown`** — fallthrough.

## plan.md shape

A document the user reads top-to-bottom and signs off on (or
overrides). Sections:

```
# Migration plan M-NNN-<date>

## Summary
- N docs discovered, total bytes
- Per-shape breakdown: plan-shaped X, mixed Y, flavor Z, ...
- Sources: project/, README.md, ...

## Per-doc table
| Path | Shape | Last touched | Lines | Cross-refs | Action |
|---|---|---|---|---|---|
| project/v2-plan.md | plan-shaped | 2026-04-12 | 142 | 3 in / 7 out | migrate to plans/ |
| ... |

## Detail (one section per doc)
### project/wiki-plan.md
- shape: plan-shaped
- signals: ... (compact rendering of imp signals output)
- proposed action: supersede (already deprecated); migrate to plans/archive/
- notes: ...
```

## plan.json shape

Direct serialization of the per-doc records the sniff produced.
Phase 2 reads this; humans don't. One record per discovered doc:

```json
{
  "migration_id": "M-2026-05-10-1530",
  "started_at": "...",
  "sources": ["project/", "README.md", ...],
  "docs": [
    {
      "path": "project/v2-plan.md",
      "shape": "plan-shaped",
      "signals": { /* full Signals.cs output */ },
      "sniff_reason": "single H1, 6 H2s, frontmatter state, title contains 'plan'",
      "proposed_action": "migrate-to-plans"
    }
  ]
}
```

## Build order

1. `imp migrate` command shell + arg parsing. Discovery + per-doc
   Signals invocation. Smoke-test: run on imp's `project/`, confirm
   per-doc records produced.
2. Sniff heuristic. Add to per-doc records. Smoke-test: most docs
   in imp's `project/` should classify as `plan-shaped` or `mixed`,
   not `unknown`.
3. plan.md + plan.json writers. Run on imp's `project/`, eyeball
   plan.md for "would I make these decisions myself?".
4. Polish: `--include` / `--exclude` globs, `--out` for test runs,
   error messages.
5. Dogfood pass: read the plan.md for imp/`project/` and note where
   the sniffer was wrong. File those misclassifications as
   learnings/notes for Phase 2 prompt design.

## Done when

- `imp migrate` runs against imp's `project/` and produces a
  `plan.md` you'd be willing to hand to Phase 2.
- Wiki-related docs (`project/wiki-plan.md` and the rest already
  identified by `plans/wiki-deprecation.md`) classify correctly as
  supersede / archive candidates — even if the sniffer alone can't
  decide that, the signals it surfaces should make the call obvious
  to the human reviewer.
- Sniff misfires, when they happen, are graceful: marked `unknown`
  rather than confidently mislabeled.
- Re-running on the same input produces the same plan.json (modulo
  the M-NNN id and timestamps) — pure function over repo state.

## Open questions

- **M-NNN id format.** Spec says `M-NNN-<datestamp>`. Use
  date-based (`M-2026-05-10-1530`) to avoid a global counter store?
  Or NNN as a local-to-`_migration/` counter (find max existing,
  +1)? Lean date-based for v0 — sortable, no state file needed.
- **Should `CLAUDE.md` parsing strip the substrate-managed section?**
  Yes per spec, but the section heading marker is in
  `Substrate/ProjectInit.cs` (`ClaudeMdHeading`). Need to expose it
  for migrate to use. Trivial.
- **What if the substrate already has hand-authored content
  overlapping a candidate doc?** Phase 1 doesn't draft proposals,
  so collision detection is Phase 2's problem. Phase 1 just notes
  what's there.
- **Do we exclude already-deprecated content?** `project/wiki-plan.md`
  has a deprecation banner now. Phase 1 should pick it up like any
  other doc; the banner is just a signal. The user's review of
  plan.md is where "yes, archive this one" gets decided.
