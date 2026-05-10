# Project substrate — a living project-knowledge system

> Working document. Captures the design conversation through
> 2026-05-09. Implementation surface is hybrid: Claude Code skills
> orchestrate, imp provides primitives. The existing imp wiki mode
> (`imp wiki`, see `wiki-plan.md`) is one of those primitives, not
> the predecessor of this system.

## The actual problem

The user's goal: **a better way to organize living project
knowledge** — an opinionated structure plus tooling to keep it
healthy — that survives across projects. dreamlands is the proving
ground; it has the right shape of pain (large codebase, complex
incremental build, UX rules, ~150 legacy markdown docs much of which
has drifted) but its current `project/` layout is *not* the model. It
is the patient, not the gold standard. The current concern-mixing
in dreamlands is bad; even self-labeled "decisions" docs are
unreliably stale (see decisions log).

In a few months when dreamlands ships and the user starts something
new, this work should give them a way-of-working they can pick up
again. Reusability across projects is a primary requirement, not a
nice-to-have.

## Purpose: the substrate for human + Claude + imps

The project-knowledge system is not a standalone artifact. It is the
**shared working substrate** between three participants:

- **The human** — directs, curates, makes judgment calls, signs off
  on irreversible changes.
- **Claude (foreground)** — collaborates with the human on
  architecture, design, big-picture decisions; reads from the
  substrate to stay grounded; writes to it under human direction.
- **Imps (background)** — handle grunt work: routine code,
  boilerplate, unit tests, design-doc research, internet research,
  double-checking, lint passes. Read from the substrate to do their
  jobs without inventing fresh patterns; produce *candidate* outputs
  that flow back into the substrate after promotion.

Without the substrate, imps either invent fresh patterns each
invocation (boilerplate that doesn't match anything) or produce work
that contradicts what was said to be wanted. The substrate is what
lets background work scale without compounding inconsistency.

The user and Claude become an I/O bottleneck if imps can't *find*
what they need from the substrate alone — every invocation needs
context staged by hand. The substrate has to be good enough that
imps can self-serve. That's a high bar and a real design constraint.

## Three deliverables

1. **The structure.** An opinionated layout, frontmatter conventions,
   indexing, classification grain. The equivalent of what spec-kit's
   `.specify/` provides for forward feature work — but for *living*
   project knowledge: rules / aspirations / learnings / plans / tasks
   / concepts / reference. Load-bearing artifact; the tools serve it.
2. **The migration.** A one-shot, lossy, human-in-the-loop tool that
   takes a clutter directory (dreamlands' `project/`) and proposes a
   structured equivalent. Lossy by design — some legacy content is
   wrong and should be dropped. The model's job is to claw structure
   out of the clutter; the human's job is to confirm and edit.
3. **The maintenance.** Ongoing tools that keep the structure healthy
   as code and intent evolve: lint (rule violations, contradictions),
   drift detect (typed by kind — see H3), concept-page regeneration
   (H2), promotion/demotion candidate flagging (TODO outgrew its
   container; section was superseded), `log.md` rollup, **plus
   integration of imp outputs back into the substrate** (build POWs
   feed state updates and rule-compliance checks; research reports
   produce candidate learnings; review imps produce typed drift
   findings — see H6).

The auto-generated concept pages aren't the system — they're a *view*
produced by maintenance over the structured store. Likewise, the
existing `imp wiki` mode (auto-generate state pages from code) is one
operation the maintenance layer performs — covering the *state* leg
of "what we want / know / have / doing."

## Goal restatement

The structured store gives a reader, in one coherent surface:

- *what we want* (aspirations, design intent)
- *what we know* (rules/constraints, learnings)
- *what we have* (the actual code, structure, behavior — auto-generated)
- *what we're doing* (active plans, in-flight tasks)

with the gaps between those four visible rather than hidden, and the
authority on each kind of claim explicit (a state claim defers to
code; an aspiration claim defers to a design doc the human owns).
See H0.

## Seed material

The conversation kicked off from Andrej Karpathy's "LLM Wiki" gist:
https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f

Karpathy's pattern is general-purpose personal knowledge curation
(articles, podcasts, journal entries) — three layers (raw / wiki /
schema), three verbs (ingest / query / lint), an `index.md` and
`log.md`, and crucially a wiki that **compounds over time** rather
than re-deriving from sources on each pass.

Some ideas port to a code SoT; some don't. See "ported / not ported"
below.

## Adjacent: spec-kit

`github/spec-kit` (forward-looking spec-driven development for new
features: Constitution → Specify → Clarify → Plan → Tasks → Implement)
is a separate concern from the wiki, but it reshapes how we think about
"legacy docs." Two pieces are directly relevant:

1. **Doc taxonomy.** spec-kit distinguishes *constitution* (rules,
   principles, UX policies — slowly evolving, hand-curated, the doc
   *is* the truth), *spec* (per-feature intent, frozen at ship —
   permanent historical record), *plan/tasks* (per-feature
   implementation, ephemeral), and implicitly leaves *state* (what the
   code currently is) and *reference* (build/tooling docs) outside its
   model. dreamlands' ~150 mixed legacy docs are some blend of all
   five. The wiki's relationship to each *kind* is different (see H0).

2. **Constitution as a primitive.** `.specify/memory/constitution.md`
   is a first-class home for project-wide rules. The other integration
   doc (`spec-kit-integration.md`) proposes injecting it as ambient
   build-mode context. For the wiki, it's also a **first-class input
   to concept pages** ("UX rules" likely sources from constitution
   directly) and a **target of lint** (does the code obey the rules?).

3. **`/speckit.analyze`.** spec-kit already has a "cross-artifact
   consistency check" stage. That's our lint operation in spirit. We
   can borrow the framing rather than invent it.

What's *not* relevant here: spec-kit's task→implement flow, `[P]`/`[US1]`
markers, the CLI itself. Those are build-mode concerns and live in
`spec-kit-integration.md`.

Whether dreamlands ever adopts spec-kit is a separate decision (Q7).
Even without adoption, the taxonomy and constitution-as-primitive ideas
shape v1.

## v0 baseline (what we already have)

From `wiki-plan.md`, shipped:

- `wiki/<path>.md` mirrors the directory tree 1:1.
- Per-page `source_tree_sha` cache → unchanged dirs skip on re-run.
- `wiki/README.md` is model-rendered (item 11a, post-v0 had landed).
- Adaptive splitting via orchestrator-model cluster proposal (item 11).
- Sidecar archive at `<repo>.wikis/W-NNN/` with manifest + transcripts.
- Research mode (`fs` + survey-tuned prompt) is the per-directory
  executor.
- Renderer is intentionally pure: deterministic transform from
  `report.json` to markdown. No model judgment at render time.

These remain the right primitives for "what's in this directory."

## Hypotheses for v1

These are *probable directions*, not decisions. Each is open in some
dimension — see "open questions."

### H0. The canonical taxonomy of project docs

The lived experience of dreamlands' `project/` (per the user) reveals
five kinds of content, often co-mingled within a single file:

| Kind | Truth in | Drift vs. code | Lifecycle | Wiki's relationship |
|---|---|---|---|---|
| **Rules / constraints** (design system, file formats, function shape) | The doc | **Alarm.** Code that violates is a bug to investigate. Internal contradictions = trouble. | Slow, hand-curated. Changes are expensive. | Surface in concept pages; lint code against. |
| **Aspirations / intent** (what we're going for, design tensions) | The doc | **Expected.** Code falls short by definition. Internal contradictions are *normal* ("deep but intuitive"). | Long-lived; revised as understanding evolves. | Hold the aspiration↔reality gap explicitly on concept pages. |
| **Learnings** (playtest feedback, sim results, articles) | The world (external evidence) | **Awareness, not alarm.** Snake-in-the-yard: not a rule, not a fact about now, but a possibility you carry forward. Decays in *relevance*, not in truth. | Aging. Old learnings fade but don't go false. | Index. Surface contextually. Possibly half-life or relevance score. |
| **Plans** *(primary working document)* — features, refactors, investigations, audits | The doc | Depends on state. | Mixed: exploring, active, shipped, shelved, abandoned. | Index by state. Don't lint shipped/shelved/abandoned against code. See H10 — plans are where most new work originates and accumulates. |
| **Task progress** (TODO items, partial checklists) | The doc + the code | Implicit drift = "it's not done yet." | Volatile. Items get promoted to plans; plans get demoted to background. | Roll up. Flag promotion candidates (a TODO that's grown teeth). |

Two kinds spec-kit's taxonomy doesn't have: **aspirations** and
**learnings**. Both are critical — aspirations describe the design
tension space the project lives in (and *should* contradict reality);
learnings describe the cumulative empirical knowledge that shapes
caution and attention without being rules.

The "snake in the back yard" framing (user's words): *"You wouldn't
assume we want a snake there, and you wouldn't assume the snake is
likely to be there a week from now. But you do now know snakes can
exist in the yard, so you might be careful going forward."*

**Implication for v1:** drift semantics are per-kind. Only rules-drift
fires the alarm; aspiration-drift is informative; learnings are
context, not constraints; plan-drift depends on plan state. The
single-disposition `imp drift` planned in v0 is wrong for four of the
five kinds.

**Implication for category mobility:** kinds aren't disjoint buckets,
they're nodes in a graph with promotion/demotion edges. TODO grows into
its own plan; plan gets shelved and becomes a learning ("we tried X,
here's why it didn't work"); learning hardens into a rule once
confirmed. The wiki should help spot when something's outgrown its
container.

**Implication for granularity (from sampling dreamlands' `project/`):**
the unit of classification is the *section*, not the file. Almost no
file in the dreamlands sample is purely one kind:

- `design/seed.md` mixes aspirations / rules / TBD-plans
  (the "origin doc" that's been mined for children but not pruned).
- `design/gaps.md` mixes plan / task-progress / decisions —
  `[x] DECIDED: deterministic daily loss` lives *inside* the gaps doc
  rather than being lifted to a rules doc.
- `architecture/cli_gap_analysis.md` mixes state / learnings / plan /
  drift detection (in a "Contradictions & Confusion" section the
  human wrote by hand).
- Even `TODO.md` has bullets that are 6-line research notes — task
  items that have outgrown their container.

This means: classifications attach to sections (or paragraphs), not
files. Section-level frontmatter (e.g. HTML comments
`<!-- kind: rules -->` before a section header) or a sidecar
`<file>.classifications.yaml` are the candidate shapes.

**Refinement candidate: flavor/lore briefs.** `reference/swamp_culture.md`
is aspiration about the *world's fiction*, not about the *system's
behavior*. Tight enough to warrant a sub-kind under aspirations
("worldbuilding aspirations") or its own top-level kind. Defer the
naming question until more samples are in.

### H1. Legacy docs are migration source, not ongoing input

Earlier framing was "the wiki ingests legacy docs alongside code,
forever." Refined: legacy docs are input to a *migration* (deliverable
2) that runs once-per-project, producing structured content in the new
layout. After migration the legacy `project/` is either deleted,
archived, or kept read-only as historical reference; the wiki's
ongoing inputs are the structured store + the code, not the messy
clutter.

Why this matters: ongoing ingestion of unstructured legacy docs is
expensive (every wiki run re-classifies them), brittle (classifications
drift), and rewards keeping the clutter around. A one-shot migration
forces the structural commitment.

The migration is necessarily lossy and human-in-the-loop. Even
self-labeled rules docs (see dreamlands' `design/decisions.md`) can be
out-of-date; the migration tool has to look at content + code together
and present a proposal the human edits.

**Concrete signal-gathering for migration classification.** Doc
content alone is insufficient — see decisions log for the live
counterexample (combat sketch misclassified the postures doc as
current because it was the most *polished*, when the rougher and
newer slot-RPS doc was actually authoritative and matched code).
Migration must combine at minimum:

- `git log -1` date (recency signal — necessary, not sufficient)
- Code-reference grep (terminology from the doc — does current code
  use these terms? What about archived/`tools/`/legacy paths?)
- Cross-doc references (which other docs cite this one, and what's
  the date/status of those?)
- Self-labeled status (frontmatter, "Living document", "DEPRECATED",
  etc. — weakest signal; presumed unreliable per decisions log)
- Human confirmation for the final call

The "polish trap" is named: a doc can be older-but-more-polished and
get superseded by a rougher-but-newer doc. Migration UX should
surface this explicitly when detected ("older doc reads more
authoritative; newer doc matches code — likely the newer one is
current").

### H2. Concept pages as a parallel namespace, holding the gap

Tree-mirror can't describe "the build system," "UX rules," "save
format," "sim loop," "combat" — concerns that span 4-6 dirs each *and*
draw on multiple kinds of source material. Likely:
`wiki/concepts/<slug>.md`, generated by an orchestrator pass that takes
a topic + relevant dirs + relevant legacy docs (with their kind
classifications).

Concept pages are where H0's drift-semantics-per-kind shows up most
visibly. **Strong validation from the sample:**
`architecture/cli_gap_analysis.md` is a hand-rolled concept page —
state survey + gap analysis + drift detection ("Contradictions &
Confusion") + citations. The form is right; the fact that a human
wrote one by hand and there's only one shows it doesn't scale without
automation.

A concept page should hold, structured:

- **Aspiration** (from intent docs): what we're going for, including
  contradictions that are deliberate.
- **Rules** (from constraint docs): what must be true. Citations.
- **Reality** (from code): what is actually implemented. Citations.
- **Gap** (model-synthesized): where reality falls short of aspiration,
  where rules are violated (alarm), where learnings have flagged
  caution areas.
- **Active plans** (from plan docs in active state): what's being done
  about the gaps.
- **Relevant learnings**: snake-in-yard items still considered live.

This is the most useful artifact in the whole wiki for a reader trying
to understand a complex subsystem. It's also the most expensive to
generate — it's necessarily cross-source synthesis with model judgment.

Tree-mirror pages stay deterministic. Concept pages relax the
renderer-purity rule because they're cross-source synthesis by
construction.

### H3. Drift is a headline output — but typed by kind

In the v0 plan `imp drift` is item 10 (post-v0). For dreamlands it
needs to be on every concept page and rolled up at `wiki/_drift.md`.
This is the deliverable that justifies the whole exercise.

But: there is no single "drift." There are at least three:

- **Rule violations** (code disobeys a constraint doc): alarm. Block
  merges in a future hook; surface prominently in `_drift.md`.
- **Aspiration gap** (code falls short of design intent): informative.
  Surface on the relevant concept page, not in `_drift.md`. Possibly
  feeds a "where's the work?" view that complements TODO.md.
- **Doc rot** (a legacy doc claims X, code does Y, doc isn't a rule
  or aspiration — it's stale survey content): the wiki *replaces* the
  legacy doc; old doc becomes a candidate for archive/delete.

Plans and learnings don't generate drift. Plans describe intent at a
moment in time; learnings describe possibilities, not contracts.

### H4. `log.md` for change tracking

Roll up `W-NNN` manifests into an append-only `wiki/log.md` so
"what did the wiki notice changed since last run" is a single grep.
Cheap given existing manifests.

### H10. Plans are the primary working document

Most new work in the substrate originates and accumulates as a plan,
not as a rule/aspiration/learning authored in isolation. The
sequence:

1. Idea → create `plans/active/<slug>.md` with `state: exploring`
2. Research, options analysis, prior-art, sketches accumulate inside
   the plan doc as work progresses
3. Optional: when the work outgrows one file (extensive prototypes,
   raw research dumps, html/js mocks), a companion directory
   `plans/active/<slug>/` appears alongside. Free-form contents —
   "random crap is fine" by design.
4. As the plan firms up, `state: exploring → active`. As phases ship,
   the plan tracks progress.
5. When concluded: `state: shipped | shelved | abandoned`, file (and
   companion dir if present) move to `plans/archive/`.
6. Rules, aspirations, learnings *distill out* of the plan as
   content settles — but the plan doc remains canonical for the
   work itself.

Implications:

- **One file is canonical, the dir is overflow.** Tools traversing
  the substrate look for `<slug>.md` first; an adjacent `<slug>/`
  is supplementary. Frontmatter lives on the `.md` only.
- **Cross-cutting non-feature stuff still goes direct to the
  taxonomy.** A standalone decision ("UTC for all timestamps")
  lands in `rules/` directly. A standalone observation
  ("Cosmos has this latency") lands in `learnings/`. The "most new
  work starts as a plan" rule applies to coherent in-flight work,
  not isolated facts.
- **The state enum is 5 values, collapsed**:

| State | Directory | Meaning |
|---|---|---|
| `exploring` | `plans/active/` | pre-decision; research and shaping |
| `active` | `plans/active/` | committed; in progress |
| `shipped` | `plans/archive/` | done; lives in codebase |
| `shelved` | `plans/archive/` | paused; might pick up later |
| `abandoned` | `plans/archive/` | tried or evaluated; kept for the lesson |

This collapsed (no separate `features/` namespace) after seeing
nb's actual `Features/` dir — single-file flat pattern, mature
mix of in-flight and shipped, ~16 files spanning user-facing
features ("MCP_OAuth", "Skills") and internal work ("Testing",
"Dependency_Upgrade", "Console_Output_Audit"). Empirically the
distinction between "feature" and "plan" wasn't load-bearing.

### H8. Trust model and write privileges

Operational rules for who can modify what (resolved 2026-05-09):

| Actor | Substrate proper | imp private scratch | Proposal sidecar |
|---|---|---|---|
| Human | read/write | n/a (don't peek) | read/write/approve |
| Foreground Claude | read/write | n/a (don't peek) | read/write/approve |
| imp (any mode) | **read-only** | read/write (private) | write proposals only |

Rules:

- **imp never writes to the substrate directly.** Its output to the
  outside world is a markdown proposal file. Even when an imp sweep
  identifies an obviously-correct fix, the proposal is the artifact;
  application is gated on approval.
- **imp gets a truly private work area.** Sidecar-style (matching
  existing `<repo>.researches/` / `<repo>.worktrees/`), invisible to
  normal browsing. Mid-run state isn't surfaced.
- **Either Claude or the human can approve a proposal.** Approval
  flow is a separate skill (`/imp-promote`).
- **Auto-approval gradient.** Not all proposals need human eyes:
  - Always-safe (Claude auto-applies): `log.md` appends, archive
    moves (content preserved, kind unchanged).
  - Claude-approvable: new learning entries, concept page
    regeneration, candidate-flag-up entries.
  - Human-required: rules content edits, deletions, supersede
    markers, anything that loses information.

Why this shape: keeps imp's failure modes contained (a Qwen
hallucination produces a bad proposal, not a corrupted substrate),
preserves provenance (every change to the substrate has either a
human-authored or proposal-applied origin), and lets approval
throughput scale (Claude handles low-risk, human reviews high-risk
or batches).

### H9. Proposal file is the output contract

imp's deliverable from any sweep or promotion-candidate run is a
single markdown file in `<repo>.imp-proposals/P-NNN-<slug>.md`,
with structure:

- Frontmatter: id, generated_at, generated_by (imp run id), sweep
  type, status (pending / approved / rejected / applied).
- Rationale (human-readable narrative — why this proposal exists).
- Proposed changes (mechanical list — moves, creates, appends, edits).
- Previews of any new content.
- "How to apply" pointer — `/imp-promote <proposal-id>`.

Categories of proposal (initial set):

- **Promotion** — plan → rules + archive + learning, when reality
  has caught up. (User example: "5000 LOC implements this active
  plan; promote it.")
- **Demotion** — rule → plan/learning, when reality disagrees.
- **Drift alarm** — code violates rule, surface in `_drift.md`.
- **Doc rot** — section contradicted by newer section elsewhere.
- **Learning candidate** — build POW noted a surprise.
- **Concept page staleness** — sources changed; regenerate.
- **TODO promotion** — TODO grew teeth; promote to plan.

The proposal format spec is its own doc when we get to that skill.

### H6. Bidirectional flow with imp outputs as candidates

The substrate accumulates not only from code changes and human edits,
but from imp work. Each imp role contributes differently:

- **Build imps** (`imp build`): produce structured POWs. The
  maintenance layer reads POWs and (a) auto-updates state pages
  (already covered by `imp wiki`), (b) checks the diff against
  current rules and surfaces violations, (c) checks the diff against
  the active plan it was supposed to satisfy and flags drift, (d)
  optionally emits a candidate learning if the POW notes a
  surprise ("approach X didn't work because Y").

- **Research imps** (`imp research`): produce structured reports. The
  maintenance layer reads reports and (a) drafts candidate learnings
  from findings, (b) updates relevant concept pages with new
  citations, (c) flags follow-up questions for promotion to plans or
  TODOs.

- **Review/lint imps** (planned, evolves from `imp drift`): produce
  typed drift findings. The maintenance layer rolls these into
  `_drift.md` (alarms) and into informational gap lists on concept
  pages (aspiration drift, doc rot).

**Critical: imps don't commit to the substrate directly.** They
produce *candidates* — drafts in a staging area
(`<repo>.project-staging/` or similar) — that the human (or Claude
under human direction) reviews and promotes. This preserves
auditability: every change to the substrate has a provenance
(human-direct / promoted-from-imp-X / regenerated-from-code) and a
rollback path.

Auto-write is reserved for state pages (regenerable from code anyway)
and `log.md` (append-only). Rules, aspirations, learnings, and plans
are human-promoted by default.

This connects back to Karpathy's compounding-wiki pattern in a way
the earlier draft of this doc rejected. The *direction* of
compounding is different (his wiki compounds from external human-
ingested sources; this substrate compounds from imp outputs and code
changes), but the maintenance discipline is the same: the LLM does
the bookkeeping, structured propagation makes the compound auditable.

### H7. Idle scheduling, mixed model tiers

Earlier draft of this hypothesis said "the Strix Halo box is idle
~136 hours/week — that's the maintenance budget." That overstated
local compute as the advantage. The real story is more nuanced:

- **Idle is a scheduling advantage, not a free-compute advantage.**
  Running maintenance overnight matters because it doesn't compete
  with foreground human + Claude attention, not because the local
  box is free. Most synthesis-heavy work (drafting candidate
  learnings, regenerating concept pages, multi-doc drift detection)
  needs cloud models — Sonnet or Haiku — because local Qwen on Strix
  Halo can't do it well. Cloud calls cost money regardless of when
  they run.
- **Local Qwen is for what it's actually good at.** Narrow,
  read-only, citation-heavy research with tight scope — this is
  imp's existing `research` mode. Good fit. Synthesis across many
  sources, judgment-heavy classification, drafting structured
  artifacts — not Qwen's lane.
- **The maintenance executor is cloud.** Sonnet/Haiku via Claude
  Code's native subagent tooling, scheduled with the `schedule`
  skill (or `loop` for self-paced batch work). imp doesn't compete
  with this — it provides primitives that the maintenance layer
  *calls*.
- **Implementation surface is mixed.** imp owns `build` and narrow
  `research`. Claude Code skills + scheduled routines own
  substrate management, migration, drift roll-ups, concept-page
  regeneration. The "imp project" working name in this doc may end
  up describing a *Claude Code skill* with imp-callable primitives,
  not a new top-level imp command. See Q13.

Open: what selects work for idle time? Candidates — explicit queue
(human/Claude add items), lint-driven (lint surfaces its own
follow-ups), periodic full sweeps (re-classify all concept pages),
curiosity-driven (subagent picks under-investigated topics from
substrate gaps). Probably all of the above, with priority weights.

The "imps doing grunt work while you sleep" framing still lands —
just with *cloud* imps doing the synthesis work overnight, plus
Qwen handling narrow read-and-report tasks where its strengths fit.

### H5. Incremental ingest driven by `git log`

For active codebases, nightly re-runs benefit from "diff since last
wiki SHA" as a planner input. SHA-cache already handles unchanged
dirs; this would handle "which concept pages might be stale because
their constituent code moved."

## Karpathy ideas we are *not* porting

- **Query-then-file-back-as-page.** Doesn't fit a code SoT. Queries
  against the wiki belong in the parent agent; we don't need to
  persist them as wiki pages.
- **Per-ingest cross-page propagation** (Karpathy: "a single source
  might touch 10-15 wiki pages"). Tempting, but breaks regenerability
  and makes the wiki harder to validate. We get cross-cutting
  synthesis from concept pages instead, on a separate pass.

## Open questions

### Q1. Per-kind disposition for legacy docs

(Reframed from the earlier "subsume vs. sit alongside" question, which
was too coarse — see H0.) For each kind, what does the wiki do?

- **Rules**: stays, hand-curated. Wiki *uses* it (concept pages cite;
  lint enforces against code). Probably lives at `wiki/rules/` or
  similar, with a single-source-of-truth invariant. Internal
  contradictions among rules surface as a `_drift.md` alarm.
- **Aspirations**: stays, hand-curated. Wiki *threads* it through
  concept pages as the "what we want" header. Internal contradictions
  among aspirations are *not* an alarm — they're the design tension
  space.
- **Learnings**: stays. Wiki *indexes* (`wiki/learnings/` or per-topic
  rollups on concept pages). Possibly: relevance-decayed display
  ("recent / aging / archived"), but the doc itself is permanent.
- **Plans**: stays, by state. Wiki *indexes* (`wiki/plans/` with state
  in frontmatter). Active plans surface on concept pages as
  "in-flight"; historical ones as "history"; shelved ones available
  but quiet.
- **Task progress**: stays in TODO.md or alongside plans. Wiki rolls
  up across the tree into a single view. Flags items that have
  outgrown their container (promotion candidates).
- **State / survey**: superseded. Once a wiki page covers the same
  scope, the legacy version is deprecated (pointer comment) or
  deleted by the user.
- **Reference** (build, tooling): stays. Wiki lints for drift between
  the doc's claims and the actual scripts/configs.

Status: **open**. Gates H1. Almost certainly needs a one-time
classification pass on dreamlands' `project/` (manual, model-assisted,
or hybrid) to bootstrap.

### Q2. Concept page scoping

- A topic = slug + relevant dirs + relevant legacy docs. How is the
  topic list bootstrapped — hand-declared, discovered from legacy
  doc filenames, or proposed by an orchestrator-model lint pass?
- Status: **open**. Probably hybrid: declared in
  `wiki/concepts/_topics.yaml` or similar, with a lint pass
  suggesting additions.

### Q3. Trust hierarchy

- Default: code > legacy docs > LLM synthesis.
- But `CLAUDE.md` / repo conventions sometimes describe rules that
  *should* override what code does today (e.g. "we're migrating away
  from X"). How does the wiki encode "code does X but the rule is
  Y"?
- Status: **open**.

### Q4. Page authoritativeness markers

- Frontmatter `coverage: full | partial | stub`? `supersedes:`
  pointing to legacy doc paths? `last_drift_check_sha`?
- Status: **open**, but probably easy once Q1 lands.

### Q5. Incremental ingest design

- `git log <last-sha>..HEAD -- <path>` as a planner input alongside
  `git ls-tree`. Concept pages whose constituent dirs changed get
  re-run.
- Status: **open**. May not be needed for v1; SHA cache may suffice.

### Q6. Command shape

Working name: `imp project`. Likely subcommands:

- `imp project init` — set up the structured area in a new repo.
- `imp project migrate <legacy-dir>` — propose structured layout
  from clutter. Human-in-the-loop, iterative.
- `imp project lint` — health check (typed drift, internal
  contradictions, orphan references).
- `imp project sync` — regenerate concept pages, indices, `log.md`.
  Subsumes the existing `imp wiki` for the state leg.

The existing `imp wiki` continues to exist as a callable operation;
`imp project sync` invokes it for the state pages. Possibly we
deprecate the standalone `imp wiki` later, but not in the first
shipping version.

Status: **open** on names and exact subcommand boundaries. The shape
above is a strawman.

### Q13. Implementation surface: imp commands, Claude Code skills, or hybrid?

**Resolved 2026-05-09: hybrid. Refined later same day.** The boundary
is **synthesis vs. static**, not "substrate vs. primitive."

- **imp CLI** owns operations that are deterministic file ops with
  no LLM-shaped work — `imp build` (unchanged), `imp research`
  (unchanged + optional `--substrate-aware` ambient context),
  `imp wiki` (state-leg generator), and **`imp init`** (substrate
  scaffold; templates as embedded content; ~60ms per run).
- **Claude Code skills** own operations that need synthesis,
  judgment, or interactive review — `/imp-promote` (interactive
  proposal review), `/project-migrate` (multi-signal classification
  of legacy docs, planned), `/project-sync` (cross-source concept
  page synthesis, planned), `/project-lint` (drift detection +
  triage, planned). Skills orchestrate cloud subagents
  (Sonnet/Haiku) and call imp primitives where citation-heavy
  research with Qwen fits.
- **Scheduled routines** via the existing `schedule` skill:
  nightly cheap lint, weekly full sync, on-demand migration.

**Heuristic:** if you find yourself writing the same template content
on every invocation, you're using the wrong tool — it's an `imp` CLI
command. If the operation needs LLM judgment per invocation
(classification, synthesis, interactive review), it's a Claude Code
skill. See the `feedback_skill_vs_imp_boundary` memory entry for the
full reasoning, traced from the original `/project-init`-as-skill
mistake.

### Q12. Top-level layout: by-kind or by-topic?

```
Option A — by-kind:                Option B — by-topic:
project/                           project/
├── rules/combat.md                ├── combat/
├── rules/trade.md                 │   ├── rules.md
├── aspirations/combat.md          │   ├── aspirations.md
├── aspirations/trade.md           │   ├── learnings.md
├── learnings/...                  │   └── plans/
└── concepts/combat.md  (gen'd)    ├── trade/
                                   │   └── ...
                                   └── _meta/concepts/  (gen'd)
```

By-kind is friendlier to per-kind operations ("show me all rules");
worse for human browsing of a topic. By-topic is the inverse.

The hybrid likely to win: **by-kind storage + by-topic concept pages
auto-generated** — humans browse the topic concept page; the
underlying source-of-truth lives by kind so per-kind tooling is easy.
This also fits H6: imps need both query shapes — "fetch all current
rules" (by-kind) and "fetch everything about combat" (by-topic
concept page).

Status: **open**. Probably the load-bearing structural decision.

### Q8. Learnings: aging and surfacing

Learnings don't go false but they decay in *relevance*. A playtest note
from two years ago against a system that's been rewritten is probably
no longer useful; one from last month probably is. Options:

- **Manual archive**: human moves stale items to an archive section.
  Cheap, requires discipline that has historically not happened.
- **Frontmatter half-life**: each learning has a `relevance_horizon`
  (e.g. 90 days) after which it dims in the rendered view but
  remains greppable.
- **Anchored to topic**: each learning is tagged with the concept(s)
  it touches. When the concept page is regenerated and the code
  underneath has changed substantially, the wiki flags learnings as
  "may be stale, please confirm."
- **Confirmed-status**: a human can mark a learning "still live" or
  "no longer applies"; default decays to "uncertain" after time.

Status: **open**. The "anchored to topic" option is most native to the
wiki shape; "confirmed-status" composes well with it.

### Q9. Plan-state encoding and category mobility

A plan can be active / shelved / historical / abandoned-but-kept /
promoted-from-todo. A TODO can be tiny / outgrown-its-container.
These need encoding:

- Frontmatter `state:` field on plans? Free-form or enum?
- Promotion: how does a TODO line become a plan? Manual (human
  extracts), model-assisted (lint flags promotion candidates), or
  both?
- Demotion: a plan that gets abandoned but kept — does it become a
  learning? Stay a plan-in-state-archived?

Status: **open**.

### Q10. Classification granularity and authoring UX

Sample evidence (H0) shows classifications must attach to sections,
not files. Options:

- **Inline HTML-comment markers**: `<!-- kind: rules -->` before a
  section heading. Cheap, visible in source, survives editing.
- **Sidecar `<file>.classifications.yaml`**: keeps source clean but
  drifts out of sync the moment someone edits the markdown.
- **Frontmatter section list**: explicit array of `{heading, kind}`
  in YAML. Centralized but brittle.
- **Inferred-then-cached**: a model classifies on first wiki pass,
  the human reviews/edits, classifications cached in a sidecar (with
  source-section hash for invalidation).

Status: **open**. The inferred-then-cached path is the most
ergonomic — bootstraps for free, decays gracefully — but adds a
human review step that has to actually happen.

### Q11. The "living document" antipattern

`design/seed.md` and `design/gaps.md` are visible cases: an origin doc
spawns child docs that harden specific decisions, but the parent isn't
pruned. The parent's stale TBDs and superseded sections rot in place.
Should the wiki:

- **Detect** it (lint pass: "this section in `seed.md` claims X TBD,
  but `decisions.md` decided X — superseded")?
- **Annotate** it (deprecation pointers in the legacy doc)?
- **Replace** it (concept page subsumes the canonical content; legacy
  doc becomes archive)?

Status: **open**. Probably all three, in that order — detect first,
annotate, then on user approval replace.

### Q7. Native spec-kit awareness?

If dreamlands ever adopts spec-kit, a `.specify/` tree appears with
`memory/constitution.md`, `specs/[NNNN]-[name]/{spec,plan,tasks}.md`,
templates, etc. Should imp wiki know about this layout natively
(treating `.specify/specs/*/spec.md` as kind=spec, `.specify/memory/
constitution.md` as kind=constitution, etc.) or stay layout-agnostic
and require explicit classification?

- **Native**: zero-config for spec-kit users; couples imp to spec-kit's
  layout.
- **Agnostic**: classification is always explicit (frontmatter tag, or
  a `wiki/_classifications.yaml`); spec-kit users just write a small
  rules file.
- Status: **open**. Probably agnostic-with-a-spec-kit-preset is the
  right shape — same pattern as the rest of imp's spec-kit pairing
  (loosely coupled, presets where useful).

## Decisions log

- **2026-05-09. Canonical taxonomy of project docs adopted.**
  Five kinds: rules / aspirations / learnings / plans / task-progress
  (plus state and reference for completeness). Drawn from the user's
  lived experience with dreamlands' `project/`. Replaces the
  earlier spec-kit-derived taxonomy, which had no place for
  aspirations or learnings. See H0 for the table and the
  drift-semantics-per-kind consequence.
- **2026-05-09. The wiki is a project SoT, not just a code SoT.**
  Aspirations and learnings aren't truths about code; they're truths
  about the project around the code. The wiki unifies all of them.
- **2026-05-09. Classification grain is the section, not the file.**
  Sampled 8 dreamlands `project/` files; almost none are purely one
  kind. The taxonomy attaches at the section/paragraph level. See H0
  granularity note and Q10 for authoring options.
- **2026-05-09. Concept-page form validated by hand-rolled example.**
  `dreamlands/project/architecture/cli_gap_analysis.md` is a human-
  written concept page (state + gaps + drift). Existence proves the
  form; non-replication proves it doesn't scale unaided.
- **2026-05-09. Reframe: this is a project-knowledge system, not
  "wiki v1."** Three deliverables (structure / migration /
  maintenance), reusable across projects, with dreamlands as proving
  ground. The existing `imp wiki` is one operation in the
  maintenance layer (the state leg), not the lineage. Working name:
  `imp project`. See "Three deliverables" section.
- **2026-05-09. Even self-labeled "rules" docs in legacy projects
  can be unreliably stale.** dreamlands' `design/decisions.md`
  self-labels as "concrete specifications locked down" and is
  "waaaaaay out of date" per the user. Migration tooling can't
  trust labels — must reconcile against current code and the
  human's confirmation. Implies migration is lossy and
  human-in-the-loop by design.
- **2026-05-09. The substrate exists to enable human + Claude + imps
  to collaborate without compounding inconsistency.** Imps in the
  background need to find rules, aspirations, plans, and learnings
  on their own; otherwise the human and Claude become the I/O
  bottleneck for every imp invocation. This is the *purpose* of the
  whole exercise; everything else is in service of it.
- **2026-05-09. Bidirectional flow: imp outputs are candidate inputs
  to the substrate.** Build POWs feed state-page updates and rule
  checks; research reports produce candidate learnings; review imps
  produce drift findings. Imps never commit directly — promotion is
  human/Claude-mediated, with provenance preserved for auditability.
  Reconnects to Karpathy's compounding-wiki pattern; the direction
  of source production is what differs. See H6.
- **2026-05-09. Implementation surface resolved: hybrid (imp
  primitives + Claude Code skills + scheduled routines).** imp
  owns `build`, `research`, `wiki` (callable). Claude Code skills
  own `/project-init`, `/project-migrate`, `/project-lint`,
  `/project-sync`, `/imp-promote`. Scheduled routines drive
  nightly/weekly maintenance. Substrate lives in-repo
  (`project/` or configurable); staging in sidecar
  (`<repo>.project-staging/`), matching imp's existing
  `<repo>.worktrees/` / `<repo>.researches/` pattern. See Q13.
- **2026-05-09. Plans are the primary working document; no separate
  `features/` namespace.** Most new work starts as a plan in
  `state: exploring` and accumulates content as it firms up. Other
  kinds (rules/aspirations/learnings) are mostly distilled outputs.
  A plan is always a `.md`; an optional companion dir
  `plans/<status-dir>/<slug>/` holds overflow material — html/js
  prototypes, scratch experiments, raw research — free-form and by
  design "random crap is fine." State enum collapsed to 5:
  exploring / active / shipped / shelved / abandoned. Decision after
  reviewing nb's empirical Features/ pattern (single-file flat,
  ~16 files spanning user-facing and internal work). See H10.
- **2026-05-09. Trust model: Claude writes substrate; imp proposes
  only.** Foreground Claude can edit substrate directly. imp is
  read-only against substrate, gets a private work area, and emits
  proposal markdown files for review. Either Claude or human
  approves; auto-approval gradient applies (log appends auto;
  rules edits human-required). See H8.
- **2026-05-09. Proposal file is imp's output contract.** imp's
  deliverable from sweeps and promotion runs is a single markdown
  file at `<repo>.imp-proposals/P-NNN-<slug>.md`. Categories:
  promotion / demotion / drift alarm / doc rot / learning candidate /
  concept staleness / TODO promotion. See H9.
- **2026-05-09. Bidirectional flow operationally:** foreground
  Claude reads substrate → writes contract → runs imp →
  `/imp-promote` drafts candidates → human reviews → promoted
  content enters substrate with provenance. Closes the H6 loop
  with concrete steps.
- **2026-05-09. The "polish trap" is real and named.** During the
  combat worked example, classified `design/combat.md` (postures,
  Feb 2026) as the most recent design exploration based on its
  prose polish and conceptual elegance. User corrected: it's the
  oldest of the combat docs; `design/super_rps.md` (May 2026,
  rougher and prototype-focused) is what's actually shipping in
  `lib/Combat/`. Doc polish does not predict currency — sometimes
  predicts the opposite (a doc gets refined when it's about to be
  set aside). Migration must use git dates + code-reference grep,
  not doc content alone. See H1 update.
- **2026-05-09. `imp init` is a CLI command, not a Claude Code skill.**
  Built /project-init as a skill first; user pushed back when it
  took ~5 minutes per run because the LLM was regenerating ~14
  static template files on each invocation. Re-implemented as
  `imp init` in `Substrate/ProjectInit.cs` with templates as content
  files in `Substrate/Templates/` shipped next to the DLL. Now runs
  in ~60ms. Boundary refined: synthesis-vs-static, not
  substrate-vs-primitive. See Q13 (revised) and the
  `feedback_skill_vs_imp_boundary` memory entry. Other planned
  skills (promote/migrate/sync/lint) are still skill-shaped — they
  need LLM-shaped work.
- **2026-05-09. Maintenance layer is Claude Code skills + cloud
  subagents, not new imp commands.** imp's strength is narrow
  headless primitives (build in worktree, citation-heavy research
  with Qwen). Synthesis-heavy substrate work needs cloud models
  (Sonnet/Haiku) and Claude Code's existing subagent tooling beats
  anything imp could build. Idle compute is a *scheduling*
  advantage (don't block foreground), not a free-compute advantage —
  cloud calls aren't free. See H7 (revised) and Q13.

## References

- v0 plan: `project/wiki-plan.md`
- v0 code: `Wiki/`, `Prompts/research-fs-wiki.md`,
  `Prompts/wiki-cluster-proposal.md`,
  `Prompts/wiki-index-synthesis.md`
- Karpathy "LLM Wiki" gist:
  https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f
- spec-kit: https://github.com/github/spec-kit
- Adjacent imp doc: `project/spec-kit-integration.md` (build-mode
  pairing — different angle on the same toolkit)
- Target codebase: `~/repos/dreamlands` (separate repo, not vendored)
