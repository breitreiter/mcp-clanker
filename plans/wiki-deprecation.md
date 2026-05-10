---
state: decided
decided: 2026-05-10
---

# Deprecate `imp wiki`

## Decision

`imp wiki` and the `Imp.Wiki` namespace are deprecated. They are kept in
the tree as a reference for ongoing substrate work but should not be
invoked, extended, or relied on. Removal is pending — date TBD, after
the substrate (`imp/`, maintained by `imp tidy` / `imp note`) reaches
parity on the use cases wiki was meant to cover.

## Why

Wiki and substrate solved overlapping problems with different shapes.
Once the substrate landed, the boundary between them stopped making
sense:

- **Wiki** synthesises per-directory pages from source (cache-keyed on
  `source_tree_sha`). Output is fully derivable from current code.
- **Substrate** captures what re-reading code can't recover: the *why*
  behind decisions, gotchas, captured notes, external references —
  plus per-file / per-symbol / per-feature index pages and concept
  syntheses that can enrich those derivable views.

The substrate's `_index/by-file/<path>.md` and `concepts/<topic>.md`
shapes already cover the orientation use case wiki was designed for,
with the additional ability to fold in non-derivable knowledge. Two
parallel systems with overlapping output is worse than one — readers
got confused about which to consult, and `imp wiki` runs were
generating pages that immediately competed with substrate output.

Substrate wins because it's the strictly more general system: anything
wiki produces, substrate can produce; the reverse is not true.

## What stays, what goes

**Removed:**

- `imp-wiki/` output directory (deleted 2026-05-10).

**Kept (deprecated, do not extend):**

- `Wiki/` source folder — entire `Imp.Wiki` namespace.
- `Prompts/research-fs-wiki.md`, `Prompts/wiki-index-synthesis.md`,
  `Prompts/wiki-cluster-proposal.md`.
- `project/wiki-plan.md` — original design doc.
- `imp wiki` CLI subcommand and the diagnostic helpers
  (`wiki-render-test`, `wiki-index-test`, `wiki-split-test`).

The CLI prints a deprecation warning on stderr when `imp wiki` is
invoked. The orchestrator and supporting files still work — they're
kept readable so substrate work can borrow ideas (the planner's
`source_tree_sha` cache-key trick, the manifest-resume flow, the
adaptive splitter's light-orchestrator pattern).

## Removal trigger

Delete `Wiki/`, the prompts, the CLI dispatch, the design doc, and
this plan when:

1. Substrate covers the per-directory orientation use case via
   `imp/_index/` or equivalent, and
2. Nothing in active substrate work still references wiki code as a
   pattern source.
