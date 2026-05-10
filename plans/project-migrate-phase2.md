---
state: shipped
created: 2026-05-10
updated: 2026-05-10
shipped: 2026-05-10
supersedes: []
---

# project-migrate Phase 2 — `/project-migrate` skill

Phase 2 of `/project-migrate`: model-driven classification of the
docs Phase 1 surfaced (`<repo>.imp-proposals/_migration/M-NNN/plan.json`),
producing one or more substrate-shaped proposals per doc that
`/imp-promote` can apply.

Source spec: `project/project-migrate-spec.md`. Phase 1 plan
(shipped): `plans/project-migrate-phase1.md`. Phase 1.5 (embeddings
clustering) is deferred indefinitely.

## Why a skill, not an `imp` command

The earlier draft of this plan put Phase 2 in `imp` as
`imp migrate classify`, modelled after `ResearchExecutor`. That
ignored cardinality: migrate runs ~3 times per project, and the
adoption tail is shallow. The engineering payback on
`MigrateClassifier.cs` + `finish_migration_classify` tool +
state.json + cost estimator never materializes.

Claude tokens are what the user pays for via Claude Code anyway;
trading them for the C# build is a straight win at low cardinality.
See the cardinality refinement note (captured 2026-05-10) for the
boundary update.

## Scope

**Builds:**
- `skills/project-migrate.md` — the skill the user invokes as
  `/project-migrate`. Instructs Claude to:
  1. Run `imp migrate` (Phase 1) if no recent plan.json exists, or
     consume the most recent `_migration/M-NNN/` if it does.
  2. Read plan.json and any relevant substrate frontmatter
     summaries (`imp/learnings/*.md`, `rules/*.md`, `plans/*.md`).
  3. Iterate the doc list. For each doc with a `migrate-to-*`
     proposed action:
     - Spawn a Task subagent (so parent context doesn't accumulate
       19 doc bodies). Subagent reads the doc + signals + relevant
       substrate frontmatter, returns a drafted proposal markdown
       block.
     - Parent writes the proposal to
       `<repo>.imp-proposals/P-NNN-migrate-<slug>.md`.
     - Parent pauses for user review (the default; this is the
       "one-at-a-time" mode the spec described as a flag).
  4. After all docs processed, draft a cleanup proposal listing
     drops + supersede markers.
  5. Tell user to run `/imp-promote` per proposal (or batch).

- The skill spec must capture:
  - The substrate kinds (`rule | learning | plan | reference | drop | defer`).
  - The proposal format from `project/project-promote-spec.md`
    (frontmatter + Rationale + YAML changes + named Previews).
  - The polish-trap awareness rule (flag suspect docs explicitly).
  - The signals-to-classification mapping that was meant to live in
    the system prompt of the C# version.
  - When to spawn subagents vs do work inline (subagent for per-doc
    classification; inline for cleanup proposal aggregation).

**Does NOT build:**
- No new C# code in `Substrate/` for Phase 2.
- No C# state schema. Proposals on disk are the state. Re-runs
  detect existing `P-NNN-migrate-*.md` files for the same M-NNN
  and skip those docs.
- No cost estimator code. Claude Code's own cost display covers it.
- No `finish_migration_classify` tool.
- No retry-on-terminal-error loop in C#. If a subagent fails,
  Claude tries again or escalates to the user.

## Where Phase 2 fits the static-vs-synthesis boundary

The boundary as originally written: synthesis = skill, deterministic
= imp. The cardinality refinement (this turn): low-cardinality
synthesis = skill regardless. So Phase 2 lands cleanly in skill
territory under either reading.

`imp migrate` (Phase 1) stays as the deterministic primitive in
`imp`. It's invoked per-run from the skill, but it's also useful
standalone — `imp migrate --plan-only` was the original intent and
it works fine without the skill.

## Skill orchestration shape

```
User runs /project-migrate
  ↓
Skill instructs Claude:
  1. Check for <repo>.imp-proposals/_migration/M-NNN/plan.json
     (a) Exists & fresh (today or recent commit) → use it
     (b) Otherwise → run `imp migrate` to refresh
  2. Read plan.md (human-readable) into context
  3. Read substrate frontmatter summaries via simple grep:
     - imp/learnings/*.md frontmatter blocks
     - rules/*.md frontmatter blocks
     - plans/*.md frontmatter blocks
     Just kind+title+topics+path; not bodies.
  4. For each doc in plan.json where action ≠ defer:
     a. Check if <repo>.imp-proposals/P-NNN-migrate-<slug>.md
        already exists → skip (resume)
     b. Spawn Task subagent with: doc content, signals, substrate
        frontmatter context, proposal format template
     c. Receive drafted proposal markdown
     d. Write to disk as P-NNN-migrate-<slug>.md
     e. Show user the proposal, pause for "next" / "edit" / "skip"
  5. Draft cleanup proposal listing drops/supersedes inline (no
     subagent — small task, parent context handles it)
  6. Report: N proposals drafted at <path>, run /imp-promote next
```

## Proposal format

Unchanged from the previous draft — must match the spec from
`project/project-promote-spec.md`:

```
---
proposal_id: P-2026-05-10-...
generated_at: ...
generated_by: project-migrate-skill:M-2026-05-10-...
category: migration
status: pending
auto_approval: human-required
---

# Proposal: Migrate `project/v2-plan.md` → `plans/v2-plan.md`

## Rationale
(narrative, cites signals)

## Proposed changes
```yaml
changes:
  - type: create
    path: plans/v2-plan.md
    preview: preview-1
  - type: set_frontmatter
    path: project/v2-plan.md
    set_frontmatter:
      superseded_by: plans/v2-plan.md
```

## Preview: preview-1
(YAML frontmatter + body for the new substrate entry)
```

The `generated_by` field changes from `imp-migrate:M-...` to
`project-migrate-skill:M-...` to reflect the skill is the author.

## Polish trap without 1.5

Same gap, different mitigation. The skill's per-doc subagent prompt
instructs the subagent to look for:
- High structural polish (frontmatter `status: shipped` /
  "DECIDED" markers / well-organized H2s)
- AND low recency (git silence > 60 days)
- AND no live code refs in current code

And flag the proposal's Rationale with "Polish trap suspected" so
the human reviewer sees it before applying.

Plus the default `--one-at-a-time` flow puts a human in the loop
for every proposal, which is the strongest mitigation.

## Build order

**2a — minimal end-to-end.**
- Write `skills/project-migrate.md` covering: invocation, the
  full orchestration loop, proposal format, polish-trap rule.
- Smoke test: run `/project-migrate` against imp's current
  M-NNN plan. Expect 8 plan_shaped docs to produce proposals.
- The cleanup proposal step can be a TODO in the skill for now.

**2b — cleanup proposal + section-grain for mixed docs.**
- Add the cleanup-proposal-generation step to the skill.
- Add per-section handling for `mixed` shape docs (subagent
  receives doc + signals; subagent decides whether to keep as
  one proposal or split per H2).

**2c — substrate-context refinement.**
- If the substrate context grows past what fits naturally, add
  topic-filtered context selection. Not needed at current
  substrate size (~10 entries).

## Done when

- `/project-migrate` against imp's M-2026-05-10-1855 plan produces
  proposals at `<repo>.imp-proposals/P-NNN-migrate-*.md`.
- A spot-check: `project/v2-plan.md` becomes a proposal that
  creates `plans/v2-plan.md` with `state: shipped`. Rationale
  cites Phase 1 signals.
- Wiki-related docs (`project/wiki-plan.md` and friends) classify
  as `drop` with rationale citing `plans/wiki-deprecation.md`
  (the subagent sees substrate context, recognizes wiki is
  already deprecated).
- Re-running `/project-migrate` after partial completion picks up
  un-processed docs and skips the ones with existing
  `P-NNN-migrate-*.md` files.
- `/imp-promote` applies the resulting proposals cleanly.

## Open questions

- **Subagent vs inline per doc.** Subagent isolates context and
  lets the parent stay tight; inline is simpler. Default to
  subagent for per-doc classification (the long-context-protection
  is real with 19 docs averaging 17KB); leave the cleanup proposal
  inline.
- **Where the skill source-of-truth lives.** `skills/` in the imp
  repo is the master; `imp init` doesn't currently copy skills
  into target repos. Open whether `imp init` should symlink /
  copy `skills/project-migrate.md` to `~/.claude/commands/` (or
  the user does it manually). Defer — same UX question applies
  to `/imp-promote` already.
- **Skill discoverability.** Without an `imp init` install step,
  users have to know to copy the skill. `imp migrate`'s "Next:"
  output could print a one-liner pointing at the skill (similar
  to how it currently points at this plan).
- **Collision with hand-seeded substrate.** Same as the previous
  draft — the subagent sees frontmatter summaries and can
  reasonably detect "I'd be proposing a duplicate of
  `imp/learnings/foo.md`". Flag as `defer` in that case.
