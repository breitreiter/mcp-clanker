---
superseded_by:
  - plans/project-migrate-phase1.md
  - plans/project-migrate-phase2.md
  - skills/project-migrate.md
migration_disposition: drop
migrated_at: 2026-05-10
migrated_via: project-migrate-skill:M-2026-05-10-1855
---

# `/project-migrate` — Spec

> Takes a clutter directory of mixed-kind legacy markdown docs and
> produces a batch of substrate proposals (for `/imp-promote` to
> apply). Lossy by design, multi-signal, human-in-the-loop. The
> headline operation for legacy-rich repos like dreamlands.

## Purpose

`imp init` scaffolds an empty substrate. `/project-migrate` populates
it from existing legacy material. For most real projects, the answer
to "where does the substrate's initial content come from?" is "what
we already wrote" — design docs, README sections, scattered notes,
TODO files, plan docs in various states of completion.

The challenge: legacy content is mixed-kind at the section grain (H0
+ the worked example for dreamlands), unreliable about its own
status (the polish trap; `design/decisions.md` self-labeling as
"locked down" while being out of date), and doesn't compose well
with the substrate's structured taxonomy without classification.

`/project-migrate` does the classification using cloud subagents
(Sonnet-class, per Q13/H7), surfaces its reasoning to the human, and
produces proposals that flow through the existing
`/imp-promote` apply path.

## Scope

**Does:**
- Walks a legacy directory (or set of paths), discovering markdown
  docs to consider.
- For each doc, gathers multi-signal evidence: git history
  (`git log --follow`), code-reference grep, cross-doc references,
  self-labels (frontmatter, "Status:" lines), content shape.
- Classifies at the right grain: per-doc when a doc is one coherent
  artifact (a plan), per-section when a doc mixes kinds.
- Drafts proposals (one per resulting substrate entry, plus a
  meta-proposal that lists what to drop/archive/supersede in the
  legacy tree).
- Tracks migration state at `<repo>.imp-proposals/_migration/M-NNN/`
  so re-runs are idempotent and resumable.
- Reports cost estimate before doing the expensive synthesis pass.

**Does not:**
- Modify legacy content directly. All output is proposals.
- Apply proposals — that's `/imp-promote`'s job.
- Decide for ambiguous cases without human input — surfaces
  classifications with reasoning, lets the human override.
- Synthesize concept pages — `/project-sync` does that after
  migration's outputs land.

## Invocation

```
/project-migrate                         # discover and plan from default sources
/project-migrate <path>...               # explicit source paths (files or dirs)
/project-migrate --plan-only             # produce the discovery+plan, skip classification
/project-migrate --resume M-NNN          # resume an interrupted migration run
/project-migrate --one-at-a-time         # pause for human review per doc, not batch
/project-migrate --include <glob>        # only docs matching glob
/project-migrate --exclude <glob>        # skip docs matching glob
```

Default sources discovered automatically:
- The repo's `project/` directory if it exists with non-substrate
  content (i.e., no `_meta/conventions.md`).
- `README.md` at repo root if substantial (>2KB heuristic).
- `CLAUDE.md` at repo root, *excluding* the substrate-managed
  section (the skill knows the heading marker added by `imp init`).
- Other top-level `.md` files (`TODO.md`, `NOTES.md`, etc.) if
  present.

The substrate must already exist. Refuse with "run `imp init` first"
if not.

## Procedure

Two phases, with a human-review gate between them. Phase 1 is cheap;
phase 2 is the expensive synthesis work.

### Phase 1 — Discovery and plan

1. **Find legacy sources.** Default-discover (above) or use the
   user's `<path>...` args. Resolve to a flat list of `.md` files.
2. **Catalog signals per doc.** For each doc, gather quickly (no
   model calls yet):
   - `git log -1 --format='%aI'` (last-modified date)
   - `git log --follow --format='%aI' -- <path> | head -1` (creation
     date, follows renames)
   - `wc -l <path>` (rough size)
   - frontmatter / "Status:" / "Decision:" labels (regex extraction)
   - cross-references to other docs (markdown links, file mentions)
   - section count (H1/H2 headings)
3. **Sniff doc shape.** Heuristic-classify each doc as one of:
   - `plan-shaped`: single coherent design/research artifact —
     migrate as one entry into `plans/`.
   - `mixed`: multiple sections of different kinds — per-section
     classification needed.
   - `flavor`: lore / worldbuilding / aspiration-heavy — likely
     migrates as one aspiration or learning, not split.
   - `reference`: external system docs — migrates to `reference/`.
   - `task-list`: TODO-style — migrates to `tasks/`.
   - `unknown`: needs model help.
4. **Estimate cost.** Sum approximate model tokens for the
   classification pass. Per-doc rate of ~5-15K tokens (read +
   classify + draft) × N docs. Print cost in $ at current Sonnet
   rates.
5. **Write the migration plan.** Write
   `<repo>.imp-proposals/_migration/M-NNN/plan.md` with: doc
   list, signals per doc, sniffed shape, estimated cost, and a
   per-doc proposed action (per-section / one-shot / drop / defer).
6. **Surface the plan.** Display to user. Get sign-off. With
   `--plan-only`, exit here.

### Phase 1.5 — Topic clustering (added 2026-05-09)

Single-doc signals (Phase 1) can't detect the polish trap on their
own. The trap is a *relational* property — doc X supersedes doc Y
on the same topic — which is invisible when looking at one doc at a
time. Empirical confirmation: dreamlands' `design/combat.md`
(historical postures) and `design/super_rps.md` (current shipped
combat) both look fine in isolation; their relationship only shows
when compared.

Phase 1.5 groups docs by topic so Phase 2's classifier sees
candidates *together*:

1. **Embed each doc.** Use a sentence-embedding model to produce a
   vector per doc. ~150 docs × ~5K tokens ≈ 750K tokens; trivial.
   Cache vectors in `_migration/M-NNN/embeddings.json` so resumes
   skip re-embedding.

   Two model paths:
   - **Cloud API** (`text-embedding-3-small` or equivalent): trivial
     to integrate, ~$0.015 for 150 docs at our rate.
   - **Local `qwen3-embedding`**: well-regarded as of mid-2026, runs
     on Strix Halo. Operational note: if we switch the local
     research executor from `qwen3-coder` to general-purpose
     `qwen3-32B`, both models can run concurrently on the box —
     research workloads and embedding workloads in parallel during
     idle hours. That makes local embeddings free real estate
     (no API cost, no API rate limits, and consistent with the
     existing local-Qwen story for narrow tasks).

   Either is fine for v0.1 of migrate; choice probably depends on
   whether the local-32B switch lands first.
2. **Pairwise cosine similarity.** Naive in-memory; no vector DB
   needed at this scale.
3. **Cluster.** Threshold-based grouping (e.g. similarity > 0.6
   means same topic) or HDBSCAN-style if the threshold is hard to
   pick. Each cluster is a candidate "topic group."
4. **Surface clusters in the plan.** The Phase 1 plan output
   includes the cluster groupings so the human can sanity-check
   before paying for Phase 2.

In Phase 2, the classifier sees the cluster as context: "doc X is
in a cluster with docs Y and Z about combat. Y is most recent and
most developed; X and Z look historical relative to Y. Confirm?"
Polish-trap detection happens at the cluster level, not the doc
level.

**Embeddings are leveraged elsewhere in the substrate suite**,
not just for migration. `/project-sync` needs to find related
entries when generating concept pages. `/project-lint` needs to
detect cross-rule semantic contradictions. A future
`imp research --substrate-aware` benefits from finding related
existing entries before answering. Probably warrants a shared
`imp embed <path>` primitive (or library helper) that all the
substrate skills consume — to be specced/built when the second
consumer materializes.

### Phase 2 — Classification and proposal generation

For each doc in the approved plan:

1. **Multi-signal pass.** Re-read the doc with all signals in
   context (date, code-grep, cross-refs, self-labels). The cloud
   subagent (Sonnet) gets:
   - The doc content
   - Per-section signals
   - Current substrate state (so it can spot collisions, see what's
     already classified)
2. **Classification.** For each section (or whole doc), classify as:
   `rule` | `aspiration` | `learning` | `plan` | `task` | `reference`
   | `drop` (with reason) | `defer` (escalate to human)
3. **Polish-trap check.** If the doc is well-polished but stale
   (recent git silence, no code references, superseded by a newer
   doc), flag explicitly: "this reads authoritative but signals
   suggest historical." Don't auto-classify as current.
4. **Draft proposal.** For each kept section/doc, generate a
   `create` proposal in `<repo>.imp-proposals/P-NNN-*.md` with:
   - The classified content (cleaned up, frontmatter added,
     `provenance.source: migration:M-NNN`).
   - A rationale citing the signals that drove the classification.
   - A preview of the new substrate entry.
   - For plan-shaped docs that were already shipped: state goes to
     `shipped` and the file lands in `plans/archive/`.
5. **Drop / supersede meta-proposal.** A single proposal at
   `P-NNN-migration-cleanup.md` listing legacy files to drop (with
   reason) or supersede markers to add (linking the legacy doc to
   its new substrate location).
6. **Update migration state.** Write per-doc status to
   `_migration/M-NNN/state.json` so resumes know what's done.

The user then runs `/imp-promote --batch` (or per-proposal) to
review and apply.

### Per-section classification details

When classifying within a mixed doc:

- Section = H2 (`##`) and below, until next H2 or EOF.
- Each section gets independent classification.
- The model is asked to consider whether adjacent sections should be
  merged in the resulting substrate entry (e.g., a "Why" + "How"
  pair in a rule).
- Sections that are clearly stale (TBD that's been resolved
  elsewhere; superseded by a newer doc) get `drop` with rationale.

## Signal sources (multi-signal classification)

The classification combines:

| Signal | What it tells us | Why it matters |
|---|---|---|
| `git log -1` date | when this content last changed | recency = currency (necessary, not sufficient) |
| `git log --follow` history | full lineage including renames | catches superseded docs |
| Code-reference grep | which terms in the section appear in current code | live vocabulary = live content |
| Code-reference grep (archived/legacy paths) | terms appearing only in archived/`tools/`/legacy code | terms went out of style — content likely historical |
| Cross-doc references | what other docs cite this one and their states | docs that cite each other in a stale chain are co-stale |
| Self-labels | frontmatter `status:`, "Status:" line, "DECIDED" markers | weakest signal; presumed unreliable per decisions log |
| Content polish | structural completeness, prose quality | **inverted** — high polish on low recency = polish trap |
| Content scope | "whole codebase" vs "narrow feature" | scope informs which substrate kind fits |

**No single signal is decisive.** The classifier uses all of them
and reports its reasoning. The polish trap (decisions log, 2026-05-09)
is the named failure mode where content polish was treated as a proxy
for currency; the multi-signal approach is one half of the fix. The
other half is **cluster-level comparison** (Phase 1.5) — polish-trap
detection requires looking at related docs together, not just the
suspect doc in isolation. Single-doc analysis can't see "this is
historical *relative to* that newer doc on the same topic."

(Empirical proof, 2026-05-09: dreamlands' `design/combat.md`
postures doc and `design/super_rps.md` slot-RPS doc both look
healthy in isolation. Only side-by-side does the polish trap
become visible — postures is older, less-developed-structurally,
prose-only; super_rps is newer, richly structured, code-anchored.)

## Output: proposal format

Migration proposals follow the existing format from
`project-promote-spec.md` (frontmatter, rationale, YAML changes
block, named previews).

Specific patterns for migration:

- **Per-section migration proposal**: `category: migration`, one
  `create` change per resulting substrate entry, plus optionally
  `set_frontmatter` on the legacy file (e.g., to add a "superseded
  by" marker).
- **Whole-doc plan migration**: `category: migration`, one `create`
  in `plans/<status-dir>/`, plus a `move` or `delete` of the legacy
  file (the `delete` is human-required per H8, so the proposal
  surfaces for explicit approval).
- **Migration cleanup**: a single proposal at the end of the run,
  listing all legacy files to drop or annotate. Reviewed last so
  the user has full context of what migrated and what didn't.

The `provenance.source: migration:M-NNN` field links substrate
entries back to their migration run. `_migration/M-NNN/` keeps the
plan, state, and run logs for audit.

## Auto-approval tier

Migration proposals are inherently **human-required** by tier
inference (they create files in `rules/`, `aspirations/`, etc., all
of which are human-required per project-promote-spec.md).

Exception: legacy → `learning` migrations that are clearly additive
(no overlap with existing learnings, low-stakes content) might be
claude-approvable. The default is human-required; the tier is
written into each proposal explicitly.

## Idempotency and resumability

- Each migration run has an ID `M-NNN-<datestamp>` (sortable).
- `_migration/M-NNN/state.json` tracks per-doc status:
  `pending | analyzed | proposed | applied | dropped | deferred`.
- `--resume M-NNN` reads the state file and skips already-completed
  docs.
- Re-running `/project-migrate` with no resume flag produces a *new*
  migration run (M-NNN+1) but detects already-proposed content
  (substrate entries with matching `provenance.source` from
  earlier M-NNN) and skips.
- Migration state directory survives until the user prunes; useful
  for audit even after all proposals are applied.

## Edge cases

- **Legacy doc grew during migration**: the migration was started
  against an older version of a doc that's been edited since. On
  resume, detect via git rev and re-classify the changed sections.
- **Substrate already populated**: substrate has hand-authored
  content that overlaps with what migration would propose.
  Detection: per-target-path collision check before drafting.
  Mark as `defer`; the human resolves by hand.
- **Doc references a doc we're dropping**: cross-doc reference
  detection should run *after* drop decisions are tentative, so the
  dropped-content cleanup proposal can rewrite/break links
  consistently.
- **Plan-shaped doc with active companion code**: the doc looks
  shipped but the code in question is being actively edited.
  Surface as `defer`; user decides whether it's still active or
  shipped-and-being-tweaked.
- **Empty / near-empty docs** (< 50 lines, no real content): drop
  by default, with a low-priority proposal entry.
- **Binary or non-markdown files** in legacy dirs: skip silently;
  log in migration plan.
- **Companion-dir-shaped legacy** (e.g., `design/rps_combat/`
  contains `implementation_plan.md` and other files): treat the
  parent doc as plan-shaped with a companion dir; migrate to
  `plans/active/<slug>.md` + companion at
  `plans/active/<slug>/`.

## Configuration

Optional fields in `project/_meta/config.yaml`:

```yaml
# migrate_default_sources: ['project/', 'README.md', 'CLAUDE.md', 'TODO.md']
# migrate_polish_trap_threshold: 90    # days since last commit before flagging polished docs
# migrate_max_cost_usd: 5.00           # refuse to start if Phase 1 estimates above this
```

Sensible defaults; missing config is fine.

## Open decisions (during build)

- **Cloud model choice**: Sonnet for synthesis-heavy classification.
  Haiku might suffice for clearly-shaped docs (plan/reference) and
  save cost. Open whether to route per-doc by complexity. Defer
  until first real run shows where Sonnet is overkill.
- **Per-section vs. per-paragraph grain**: spec says per-section
  (H2 boundaries). Some docs might warrant finer grain (a section
  with a `[x] DECIDED:` line buried inside, like dreamlands'
  `gaps.md`). Open whether to allow nested classification within
  a section.
- **Cost ceiling default**: $5 is conservative. dreamlands has 150
  docs of varying size; could easily be $20+. The user-facing UX
  needs to handle "this is going to cost $X, OK?" without surprise.
- **Migration-of-migration**: if a project has been migrated once
  (substrate populated from legacy), then later re-migrated against
  a *different* legacy source. Detection: substrate provenance
  shows existing `migration:M-XXX` entries; new run produces
  `M-YYY`. Should mostly Just Work but worth specifying.
- **Plan-shaped detection accuracy**: heuristic plan detection in
  Phase 1 (sniff) might mis-categorize. Phase 2 should re-check
  and correct.
- **Companion-dir handling**: when a doc has a sibling dir of
  scratch material (e.g., `design/rps_combat/implementation_plan.md`
  + the rest of `rps_combat/`), the migration should treat the
  whole subtree as one plan with a companion dir. Detection: same
  basename for `.md` and the dir.

## Build order

1. **Phase 1 (cheap) standalone.** Implement discovery, signal
   gathering, sniff, plan output. Test on dreamlands' `project/`
   without any model calls — should produce a reasonable-looking
   plan.md within a few seconds. *Already partially shipped: see
   `imp signals <doc>` (Substrate/Signals.cs).*
2. **Phase 1.5 clustering.** Embedding API call + cosine similarity
   + threshold cluster. Cache embeddings under
   `_migration/M-NNN/embeddings.json`. Likely warrants a shared
   `imp embed` primitive once a second consumer (sync/lint) needs
   it.
3. **Cost estimator.** Token-counting pass over the doc content;
   per-doc estimates summed; embedding cost added.
4. **Phase 2 classification.** Cloud subagent invocation per-doc,
   with all signals + cluster context in the prompt. Test on a
   small slice (3-5 docs) first.
4. **Proposal drafting.** Generate proposals to
   `<repo>.imp-proposals/` from classifications. Verify
   `/imp-promote` can apply them.
5. **`--resume`.** State tracking + skip-completed.
6. **`--one-at-a-time`.** Per-doc human gate.
7. **Cleanup proposal**. Generated at end of Phase 2, lists
   drops/superseded.
8. **Test on dreamlands' `project/`.** End-to-end. Validate
   classifications against user judgment; refine prompts based on
   misclassifications.
9. **Test against nb's `Features/`.** Different shape (16
   plan-shaped feature docs, mostly shipped). Should mostly produce
   `plans/archive/` proposals with `state: shipped`.

## Done when

- Phase 1 (no-model) runs against dreamlands' `project/` and
  produces a sensible plan in seconds.
- Phase 2 classifies a small slice, produces proposals,
  `/imp-promote` applies them cleanly.
- Polish-trap test case (dreamlands' postures vs. RPS) is correctly
  classified — postures-doc as historical, RPS-doc as current —
  without human intervention.
- Resume after interruption picks up where it left off.
- A run on nb's `Features/` produces 16 plan-archive proposals and
  the user can land them in one batch session.

## Notes for future iterations

- **Reverse migration**: if a substrate entry should go back to
  legacy form (rare; e.g., user decides certain docs work better as
  freeform). Probably not needed.
- **Migration from non-markdown sources** (Notion exports, Google
  Docs, Confluence dumps): out of scope for v0.1. Convert to
  markdown first.
- **Multi-repo migration**: migrate from a sibling repo's docs.
  Out of scope; one repo per invocation.
- **Continuous migration**: a long-running mode that watches the
  legacy dir and proposes migrations as content is added. Probably
  unnecessary — a one-shot followed by direct substrate authoring
  is the natural pattern.
