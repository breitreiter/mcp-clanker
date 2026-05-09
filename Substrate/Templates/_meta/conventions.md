# Substrate conventions

The single source of truth for this substrate's rules. Read this if
you're authoring an entry, building a tool that reads/writes the
substrate, or trying to understand why an entry is shaped the way it
is.

## Kinds

Every entry has a `kind`. Kinds differ in their relationship to code,
their drift semantics, and their lifecycle.

### `rule`
Locked-in constraints — design system invariants, file formats, API
shapes, anything where breaking it is alarming and contradictions are
trouble. Truth lives in the doc itself; code is expected to obey.
Drift between rule and code is an **alarm**.

Lifecycle: slow, hand-curated. Changes are expensive and deliberate.

### `aspiration`
What we're going for; design intent. Code falls short by definition.
Internal contradictions are normal ("combat should be deep but
intuitive"). Drift between aspiration and code is **informative**, not
alarming.

Lifecycle: long-lived; revised as understanding evolves.

### `learning`
Discovered knowledge from playtest, simulation, prior implementation,
external research. Not a rule, not a fact about now — an awareness
signal. Decays in relevance (over time, the area it touches may have
been rewritten), not in truth.

The "snake in the back yard" framing: not a rule that we want a snake
there, not a guarantee one's there now, but useful to carry forward.

Lifecycle: aging. Old learnings fade in surfaced views but don't go
false.

### `plan`
The **primary working document** for any coherent piece of work in
this repo — features, refactors, investigations, audits. Most new
work starts here, in `state: exploring`. Research, options analysis,
decisions, and prototyping notes accumulate inside the plan doc as
the work firms up. Other kinds (rules, aspirations, learnings) are
mostly *distilled out* of plans as content settles, not authored
independently.

A plan is normally a single file at `plans/<status-dir>/<slug>.md`.
When the planning work outgrows one file (extensive research, html/js
prototypes, scratch material, multiple sub-docs), an optional
companion directory at `plans/<status-dir>/<slug>/` may appear
alongside it. The `.md` stays canonical (it carries the frontmatter
and the plan narrative); the companion dir holds whatever the work
needs — no enforced structure, "random crap is fine" is the explicit
answer. Both move together when the plan changes state.

States (`state:` frontmatter field):

| State | Directory | Meaning |
|---|---|---|
| `exploring` | `plans/active/` | pre-decision; research and shaping |
| `active` | `plans/active/` | committed; in progress |
| `shipped` | `plans/archive/` | done; lives in codebase now |
| `shelved` | `plans/archive/` | paused; might pick up later |
| `abandoned` | `plans/archive/` | tried or evaluated; kept for the lesson |

Lifecycle: most plans walk `exploring → active → shipped`. Some get
shelved or abandoned along the way. When a plan ships, distilled
artifacts may be extracted to `rules/`, `learnings/`, etc. (with
provenance pointing back to the plan); the plan doc itself stays in
`archive/` as the canonical record of the work.

### `task`
Tracking items: TODOs, fix-its, in-flight work that doesn't warrant
its own plan yet. Lives at `tasks/` (or root `TODO.md`, configurable).

Lifecycle: volatile. Tasks may be promoted to plans when they grow
substantive scope.

### `reference`
Pointers to external systems — build instructions, tool READMEs,
third-party docs. Truth lives in the doc; the substrate lints for
drift between the doc's claims and the actual scripts/configs.

## Frontmatter

Every entry in the substrate has YAML frontmatter:

```yaml
---
kind: rule | aspiration | learning | plan | task | reference
title: short human-readable title
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human | imp-build:R-NNN | imp-research:R-NNN | imp-wiki:R-NNN | migration:M-NNN
  promoted_at: YYYY-MM-DD       # only if promoted from a candidate
  promoted_by: human | claude
---
```

Per-kind extensions:

| Kind | Extra fields |
|---|---|
| `rule` | `enforces` (optional list of file globs the rule applies to) |
| `aspiration` | (none) |
| `learning` | `relevance_horizon` (optional date past which to fade), `topics` (list) |
| `plan` | `state: exploring \| active \| shipped \| shelved \| abandoned`, `supersedes` (list of plan filenames), `companion_dir` (optional bool — set when `<slug>/` dir exists alongside) |
| `task` | `topic` (optional), `parent_plan` (optional) |
| `reference` | `subject` (what external thing this references) |

The `concepts/` namespace uses different frontmatter (auto-generated;
sources list); see `concepts/README.md`.

## Drift semantics

Drift = a claim in the substrate disagrees with another claim or with
code. The right response varies by kind:

- **Rule violation** (code disagrees with rule): **alarm**. Lint
  surfaces it. Fix the code or update the rule deliberately.
- **Internal rule contradiction** (rule A vs. rule B): **alarm**.
  The substrate is inconsistent.
- **Aspiration gap** (code falls short of aspiration): **informative**,
  not an alarm. Surfaces on the relevant concept page.
- **Aspiration internal contradictions**: **expected**. Aspirations
  describe the design tension space.
- **Learning vs. current code**: **awareness signal**. The learning
  may still be relevant, or may have been mooted by a rewrite.
- **Plan vs. code** (active/exploring plan): tracking. Drift = "not
  done yet."
- **Plan vs. code** (shipped/shelved/abandoned): noise. Suppress.
- **Reference vs. system**: **lint**. Doc claims X, script does Y —
  flag for review.

## Trust model

Who can write to the substrate:

| Actor | Substrate proper | imp scratch | Proposal sidecar |
|---|---|---|---|
| Human | read/write | n/a | read/write/approve |
| Foreground Claude | read/write | n/a | read/write/approve |
| imp (any mode) | **read-only** | read/write (private) | write proposals only |

imp never writes to the substrate directly. Output is a proposal
markdown file at `<repo>.project-proposals/P-NNN-<slug>.md` for
review and approval.

### Auto-approval gradient

When Claude is reviewing imp proposals on the human's behalf:

- **Always-safe** (Claude auto-applies): `log.md` appends, archive
  moves (content preserved, kind unchanged).
- **Claude-approvable**: new learning entries, concept page
  regeneration, candidate flag-ups.
- **Human-required**: rules edits, deletions, supersede markers,
  anything that loses information.

## Proposal categories

imp sweeps produce proposals in these categories:

- **Promotion** — a plan has caught up with reality (e.g. an active
  plan that's been fully implemented in code), should become rules +
  archived plan + learning.
- **Demotion** — a rule disagrees with reality; demote to plan or
  learning.
- **Drift alarm** — code violates a rule; surface in `_drift.md`.
- **Doc rot** — section contradicted by newer section elsewhere.
- **Learning candidate** — build POW noted a surprise; propose a
  learning entry.
- **Concept page staleness** — sources changed; regenerate.
- **TODO promotion** — task has grown substantive scope; promote to
  plan.

## Concept pages

Auto-generated synthesis pages, one per topic, regenerated by
`/project-sync`. Each pulls from relevant entries across all kinds
and presents a unified view. Don't hand-edit `concepts/<topic>.md` —
your changes will be overwritten on next sync.

Concept page frontmatter:

```yaml
---
topic: <topic-slug>
generated_at: YYYY-MM-DDTHH:MM:SSZ
generator_run_id: PS-YYYY-MM-DD-NNN
sources:
  rules: [...]
  aspirations: [...]
  learnings: [...]
  plans/active: [...]
  plans/archive: [...]
  state: { code: <paths> }
  tasks: [...]
---
```

Concept page body sections (omit any section with no source content):

- **Aspiration** — from aspiration entries
- **Rules (binding)** — from rules entries with citations
- **Current state** — from code (via `imp wiki`)
- **Active plans** — from `plans/active/`
- **Relevant learnings** — from `learnings/`, surfaced contextually
- **Open tasks** — from `tasks/`
- **Drift / contradictions** — from lint pass, typed by kind
- **Lineage** — pointers to `plans/archive/`

## Configuration

Optional `_meta/config.yaml`:

```yaml
location: project/                       # substrate location, relative to repo root
proposals: ../{{REPO}}.project-proposals/  # imp's proposal sidecar (gitignored)
tasks_path: project/tasks/               # or root TODO.md if you prefer flat
```

Defaults are sensible; missing config is fine.
