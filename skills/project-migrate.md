---
name: project-migrate
description: Use to ingest a non-empty repo's existing prose docs (project/, design notes, scattered specs) into the imp substrate. Phase 2 of the migrate flow — Phase 1 is `imp migrate` (deterministic discovery + signal-gathering), this skill is the model-driven classification + proposal-drafting pass on top of Phase 1's plan.json. Per-doc subagent dispatch with one-at-a-time human review by default. Produces proposals at <repo>.imp-proposals/P-NNN-migrate-*.md for /imp-promote to apply.
---

# /project-migrate

Ingests existing prose documentation into the imp substrate as
structured layer-1 entries (rules, learnings, plans, references) or
drops with rationale. The model-driven phase that turns Phase 1's
plan.json into actionable proposals.

This skill assumes Phase 1 (`imp migrate`) has run or will run on
demand. It does not replicate the discovery / signal-gathering work.
Substrate kinds, the polish-trap rule, and the proposal format are
defined here; the subagent prompt template at the bottom is the
load-bearing artefact.

Source spec: `project/project-migrate-spec.md` in imp's repo. Build
plan: `plans/project-migrate-phase2.md`. Companion seeding flow:
`plans/init-seeding.md`.

## When to use

- User invokes `/project-migrate` (with or without a path).
- After `imp init` on a non-empty repo, when the init-seed pass
  detected legacy content — the seed handles parent-knowledge,
  this skill handles existing-prose. Run after seeding, not
  instead of.
- User wants to re-run migration on a new source set or after
  drafting docs intended for ingestion.

When **not** to use:

- Substrate doesn't exist yet → direct user to `imp init`.
- Outside a git repo → refuse.
- No legacy content (no `project/` dir, no top-level prose docs
  beyond README/CLAUDE/TODO that aren't already substrate-shaped) →
  print "no migration sources detected" and exit cleanly.
- Cross-boundary dirs have uncommitted changes — proposals get
  applied via `/imp-promote`, which requires clean dirs. Ask user
  to commit first.

## Procedure

### 1. Resolve repo and migration plan

Get the repo root: `git rev-parse --show-toplevel`. The proposals
dir is `<dirname-of-repo>/<basename-of-repo>.imp-proposals/`.
Migration runs land at `<proposals-dir>/_migration/M-NNN-<date>/`.

Decide which `M-NNN` to operate on:

- If the user passed `--resume M-NNN`, use it.
- If the latest `_migration/M-*/plan.json` exists and was
  generated less than 7 days ago (check `migration_id` date
  component), use it.
- Otherwise run `imp migrate` to produce a fresh plan. If the
  user passed source paths (`/project-migrate path/...`),
  forward them: `imp migrate path1 path2 ...`.

Read `plan.md` and `plan.json` from the resolved migration dir.
Hold plan.json structured for iteration; surface plan.md to the
user as a quick orientation.

### 2. Gather substrate context

The per-doc subagent needs to know what's already in the substrate
so it can detect collisions ("I'd be proposing a duplicate of
`imp/learnings/foo.md`") and reference superseding entries (the
wiki-deprecated case).

Walk these dirs and collect frontmatter blocks (NOT bodies):

- `imp/learnings/*.md`
- `imp/reference/*.md`
- `imp/concepts/*.md` (auto-generated narrative views; can grow
  to overlap migration targets even if empty at init)
- `rules/*.md`
- `plans/*.md`
- `bugs/*.md`

For each file, extract the `---` frontmatter and the H1 title.
Concatenate into a single `substrate-context.md` blob to pass to
each subagent. At current substrate sizes (<50 entries) this fits
comfortably; revisit if the substrate grows past ~100 entries.

### 3. Surface the plan to the user

Show the user:

- `plan.md`'s summary (doc count, shape breakdown).
- The list of docs the skill will process (those with
  `proposed_action` not equal to `defer`).
- The estimated subagent dispatch count.
- "Proposals will land at `<proposals-dir>/`. Each is reviewed
  before the next one runs. Continue?"

Wait for user assent. If they want to skip docs or filter, accept
that ("only the v2-plan and v3-plan ones") and adjust the loop.

### 4. Per-doc dispatch loop

For each doc in the plan that the user approved:

1. **Skip-if-done check**: if a proposal at
   `<proposals-dir>/P-*-migrate-<doc-slug>.md` already exists for
   this `M-NNN` (check `generated_by` field), skip it. This makes
   re-runs idempotent.
2. **Pre-compute proposal_id**: find the max existing
   `<proposals-dir>/P-<today>-*.md` sequence number and add 1
   (zero-padded to 3 digits). `<today>` is UTC date
   (`YYYY-MM-DD`), matching `migration_id` convention. The
   `P-<today>-NNN` sequence is shared across all proposals
   written today regardless of suffix (`migrate-<slug>`,
   `migration-cleanup`, anything else) — re-scan max-of-existing
   immediately before each allocation rather than caching a
   counter, so a per-doc proposal added between steps doesn't
   collide with the cleanup proposal at end of run.
   Use the resulting id in the subagent prompt.
3. **Spawn subagent (or fall back inline)**: prefer the Agent
   tool (Claude Code's agent-spawn tool — NOT TaskCreate, which
   manages todos) with `subagent_type: general-purpose`, using
   the prompt template below ("Subagent prompt"). Pass:
   - The doc's full content (read inline)
   - The doc's record from plan.json (path, shape, sniff_reason,
     signals)
   - The substrate-context blob from step 2
   - The pre-computed `proposal_id` and `migration_id`

   **If the Agent tool is not available** (e.g., this skill is
   itself running inside a subagent, or a restricted-tool flow),
   degrade gracefully: do the classification work inline, applying
   the subagent prompt as your own internal instructions.
   Surface this to the user once at the start of the loop —
   "Agent tool unavailable; running inline. Parent context will
   accumulate doc bodies; budget accordingly." Inline runs are
   functionally equivalent but defeat the context-isolation that
   subagent dispatch was meant to provide.
4. **Receive drafted proposal markdown** as the subagent's
   return value. Validate it has frontmatter + Rationale +
   Proposed changes + Preview sections.
5. **Write to disk** at
   `<proposals-dir>/<proposal_id>-migrate-<slug>.md`.
6. **Show proposal to user** with the subagent's classification,
   polish-trap flag if any, and proposal preview.
7. **Pause for review**. User responses:
   - "next" / "ok" / "continue" → proceed to next doc
   - "skip" → delete the just-written proposal, move on
   - "edit" → user dictates revisions; rewrite the proposal,
     re-show, re-pause
   - "stop" → halt the loop; remaining docs are processed on
     re-run
   - "batch the rest" → drop the per-doc pause for remaining docs,
     just dispatch and write

### 5. Cleanup proposal

After per-doc processing, draft a single cleanup proposal at
`<proposals-dir>/<cleanup_proposal_id>-migration-cleanup.md` (use
the same ID-allocation scheme as per-doc proposals: pre-compute
`P-<today>-<NNN>` from max-of-existing+1) covering:

- Every doc with `kind: drop` from this run, with the per-doc
  drop rationale inlined.
- Every legacy doc whose content was migrated to a substrate
  entry — propose a `set_frontmatter` adding
  `superseded_by: <new-substrate-path>` so the legacy file is
  marked but not deleted (deletion is human-required per the
  promote spec).
- Optionally: docs with `kind: defer` listed for visibility, with
  the per-doc reason.

This step runs inline (no subagent) — it's aggregation work the
parent's context already has.

**Always emit the cleanup proposal, even on single-doc runs.** It
carries the supersession `set_frontmatter` markers for migrated
legacy files; skipping it leaves orphan source docs with no
pointer to their substrate destinations. The cleanup proposal can
be small (one entry, no drops) and still earn its keep.

### 6. Hand off to /imp-promote

Final report to user:

- N proposals written at `<proposals-dir>/`
- M docs processed, K skipped, L deferred
- Polish-trap suspected on docs: [list]
- Run `/imp-promote --batch` to review and apply, or
  `/imp-promote P-<id>` for a specific proposal

## Substrate kinds the skill produces

The subagent must classify each doc (or section) as one of:

- **`rule`** (`rules/<slug>.md`) — Hard project invariant. Drift
  between rule and code is an alarm. Use sparingly; most docs are
  not rules.
- **`learning`** (`imp/learnings/<slug>.md`) — Discovered knowledge,
  why-decisions, gotchas. Awareness signals that survive refactor.
- **`plan`** (`plans/<slug>.md`) — Coherent piece of work (feature,
  refactor, investigation). Has `state:` frontmatter
  (`exploring | active | shipped | shelved | abandoned`). Migrated
  docs typically land as `state: shipped` (the work happened) or
  `state: shelved` (intent recorded, never executed).
- **`reference`** (`imp/reference/<slug>.md`) — Pointer to an
  external source (URL, blog, paper) with an archived snippet.
  Most legacy docs aren't this — only adopt for docs that
  primarily archive external material.
- **`drop`** — Don't migrate. Used for stale, superseded, or
  meta-content (e.g., a TODO that's been resolved elsewhere).
  Per-doc proposal still gets written (with `category: migration`
  and a `set_frontmatter` change adding a `superseded_by` /
  `migration_disposition: drop` marker on the legacy file); the
  cleanup proposal at end aggregates all drops with their
  rationales for batch review.
- **`defer`** — Subagent isn't confident. Per-doc proposal is
  written with `category: migration_defer` and no changes block —
  just the rationale. The cleanup proposal lists deferrals for
  visibility.

There is intentionally no `aspiration` kind in v0.2 — keep the
kind set minimal until the substrate has aspirations defined.

## Polish-trap rule

The polish trap: a well-polished doc that *reads authoritative*
but is actually historical. Single-doc analysis can detect the
egregious cases via signal combination. The subagent should flag
`polish_trap_suspected: true` when ALL of:

- Structural polish high (frontmatter `status: shipped` /
  `DECIDED` markers / well-organized H2s)
- Recency low (git-touch > 60 days ago per signals)
- No live code references to the doc's terms in current code

The subagent must surface this in the proposal's Rationale
prominently — the human reviewer should see it before applying.

Note: real polish-trap detection requires cluster-context
(comparing the suspect doc against newer docs on the same topic).
That's Phase 1.5 (embeddings) and isn't built. The single-doc
heuristic catches the loudest cases; the per-doc human review pause
catches the rest.

## Proposal format

Must match `project/project-promote-spec.md` — read it for the
authoritative `## Proposed changes` verb vocabulary
(`create | move | delete | append | set_frontmatter`) and the
auto-approval tier inference rules. The shape used by migration
proposals is:

```markdown
---
proposal_id: P-2026-05-10-001
generated_at: 2026-05-10T19:00:00Z
generated_by: project-migrate-skill:M-2026-05-10-1855
category: migration
status: pending
auto_approval: human-required
---

# Proposal: Migrate `project/v2-plan.md` → `plans/v2-plan.md` (shipped)

## Rationale

The doc is a plan-shaped design document tracking the v2 build
order. Phase 1 signals: 88 lines, last touched 2026-04-21, 5
incoming cross-references, 8 H2 sections. Plan-flavored title
("Plan to reach v2") and frontmatter-less but structurally
coherent. The work has been completed (recent commits show v2
phases shipping), so the migrated entry uses `state: shipped`.

No polish trap suspected — recent git activity, live code
references in the substrate.

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

```yaml
---
kind: plan
title: Plan to reach v2
state: shipped
created: 2026-04-21
updated: 2026-05-01
shipped: 2026-05-01
provenance:
  source: project-migrate-skill:M-2026-05-10-1855
  migrated_at: 2026-05-10
---

# Plan to reach v2

<body verbatim from project/v2-plan.md, lightly cleaned of
imp-internal cruft if any>
```
```

The `provenance.source` field is what makes re-runs idempotent and
lets future passes detect "this content already migrated under
M-NNN."

## Subagent prompt

The Task subagent gets this prompt (adapt the variables in
`{{...}}` per dispatch):

> You are classifying a single legacy markdown document for
> migration into a structured project-knowledge substrate. Your
> output is a single drafted migration proposal (or `kind: drop` /
> `kind: defer` decision) — no other text.
>
> ### Substrate kinds
>
> Classify the doc (or its sections) as one of:
> - `rule` (rules/) — hard project invariant, drift = alarm
> - `learning` (imp/learnings/) — discovered why-decision, gotcha,
>   awareness signal
> - `plan` (plans/) — coherent work; `state: exploring | active |
>   shipped | shelved | abandoned`
> - `reference` (imp/reference/) — pointer + archived external
>   source
> - `drop` — stale / superseded / meta; will go into cleanup
>   proposal
> - `defer` — you're not confident; flag for human review
>
> ### The doc
>
> Path: `{{path}}`
> Phase 1 sniff shape: `{{shape}}` ({{sniff_reason}})
>
> Signals (from `imp migrate`):
> ```json
> {{signals_json}}
> ```
>
> Content:
> ```markdown
> {{doc_content}}
> ```
>
> ### Existing substrate (frontmatter only)
>
> Use this to detect collisions and reference superseding entries.
>
> ```
> {{substrate_context}}
> ```
>
> ### Polish-trap check
>
> Set `polish_trap_suspected: true` AND surface it in your
> Rationale if ALL of:
> - structural polish high (status:shipped, DECIDED markers,
>   well-organized H2s)
> - recency low (git last_modified > 60 days ago)
> - no live code refs in the signals
>
> ### Output
>
> A single proposal markdown block matching this shape. For
> `kind: drop`, omit the `Preview` section and use only a
> `set_frontmatter` change adding `migration_disposition: drop`
> to the legacy file. For `kind: defer`, omit both the changes
> block and the preview; just emit frontmatter + Rationale (the
> parent's cleanup proposal aggregates these for human review).
>
> ```markdown
> ---
> proposal_id: {{proposal_id}}
> generated_at: {{now_iso}}
> generated_by: project-migrate-skill:{{migration_id}}
> category: migration
> status: pending
> auto_approval: human-required
> ---
>
> # Proposal: Migrate `{{path}}` → <new-path> (<state if plan>)
>
> ## Rationale
>
> <Cite specific signals that drove your classification.
> Polish-trap flag goes here if applicable.>
>
> ## Proposed changes
>
> ```yaml
> changes:
>   - type: create
>     path: <substrate-path>
>     preview: preview-1
>   - type: set_frontmatter
>     path: {{path}}
>     set_frontmatter:
>       superseded_by: <substrate-path>
> ```
>
> ## Preview: preview-1
>
> ```yaml
> ---
> kind: <kind>
> title: <title>
> created: <use git first_seen date if known, else today>
> updated: <today>
> <state field if kind == plan>
> provenance:
>   source: project-migrate-skill:{{migration_id}}
>   migrated_at: {{today}}
> <touches if known: files/symbols/features>
> ---
>
> # <Title>
>
> <Body. Lightly clean the source — strip imp-internal cruft,
> normalize headings — but preserve content. If this is a `plan`
> migration, add a one-line "Outcome:" note at the top
> documenting the current state of the work if discernible from
> signals.>
> ```
> ```
>
> Constraints:
> - The substrate path must follow conventions (kebab-case slug,
>   correct dir per kind).
> - Don't fabricate touches.files — only include files you can
>   verify exist via the signals or substrate context.
> - The Phase 1 `proposed_action` (`split-and-classify`,
>   `migrate-to-plans`, etc.) is a *hint*, not a directive — feel
>   free to override it with rationale (e.g., a doc Phase 1
>   flagged as `split-and-classify` may turn out to be one
>   coherent topic and migrate as a single entry).
> - For `mixed`-shape docs (per Phase 1 sniff), if the doc has
>   sections that classify as different kinds, output multiple
>   `create` changes (one preview per resulting entry) and explain
>   the split in the Rationale. **Splitting-aggression rule:**
>   prefer one entry per coherent topic, not one entry per H2.
>   Per-H2 splits are usually too fine-grained — H2 sections of a
>   single design doc almost always belong together. Split only
>   when sections clearly classify as different *kinds* (e.g., a
>   "Rules" section and a "TODOs" section in the same doc).
> - Citations in the Rationale should reference signals or
>   substrate entries by name, not invent facts.

## Edge cases

- **Doc references a doc we're dropping.** The cleanup proposal
  should call this out so the human can fix the cross-link before
  applying.
- **Substrate already has a hand-seeded entry overlapping the
  proposed one.** Subagent should detect via substrate-context
  and emit `kind: defer` with rationale ("would duplicate
  imp/learnings/foo.md").
- **Companion-dir-shaped legacy** (e.g., `design/feature/` with a
  primary `design/feature.md` plus scratch files alongside): treat
  the parent doc as `plan` shaped with a companion dir; migrated
  to `plans/<slug>.md` with the companion at `plans/<slug>/`.
- **Doc grew during migration** (someone edited the source mid-run):
  re-running the skill detects the existing proposal but doesn't
  refresh it. Workaround: delete the proposal and re-run to pick
  up changes. Better behavior is a v0.3 concern.
- **Subagent returns malformed proposal**: parent re-prompts once
  with the validation error, then escalates to user if the second
  attempt also fails.

## Resume behavior

State lives entirely in the proposal files on disk. Re-running
`/project-migrate` (without `--resume`) auto-detects the latest
`_migration/M-NNN/` and resumes its dispatch loop, skipping docs
that already have a `P-*-migrate-<slug>.md` proposal whose
`generated_by` matches the current `M-NNN`.

To force a fresh classification of an already-proposed doc: delete
the proposal file and re-run.

To start a brand-new migration run (different source set or
significantly changed signals): run `imp migrate <new-paths>` first,
then `/project-migrate` will pick up the new `M-NNN`.
