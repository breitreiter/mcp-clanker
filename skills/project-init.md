---
name: project-init
description: Use when initializing the project-substrate layout in a repo (rules / aspirations / learnings / plans / tasks / concepts). First skill in the substrate suite. For greenfield init or projects without legacy docs at project/. For projects with existing project/ docs to be reorganized, /project-migrate (planned) is the right skill instead. Refuses if non-substrate content already exists at the target location.
---

# /project-init

Scaffolds the project-substrate layout: an opinionated directory
structure for rules, aspirations, learnings, plans, tasks, and
auto-generated concept pages, with conventions teaching foreground
Claude how to read from and write to it.

Design context lives in `<repo>/project/project-substrate-notes.md`
and `<repo>/project/project-init-spec.md` if you need it; otherwise
the procedure below is self-contained.

## When to use

- User asks to initialize / set up / scaffold the substrate, or asks
  about "project knowledge", "the structured docs thing", or similar.
- Greenfield project, or brownfield without docs at `project/`.

When **not** to use:

- Existing content at target location → refuse, point at
  `/project-migrate` (planned).
- Outside a git repo → refuse.

## Procedure

Follow these steps in order. Confirm with the user before any
filesystem write that affects existing files (`CLAUDE.md`,
`.gitignore`).

### 1. Verify git context

Run `git rev-parse --is-inside-work-tree`. If it errors, refuse:
"Substrate init only runs inside a git repo. Initialize git first
(`git init`), then re-run."

### 2. Determine substrate location

Default: `project/` at repo root.

If the user passed a path argument, use that. Confirm with the user
before proceeding ("I'll create the substrate at `<path>`. OK?").

If `<location>/_meta/conventions.md` already exists: this is a
re-init. Without `--force`, no-op and print a status report
(directory listing of `<location>/`). With `--force`, regenerate
only skill-owned files (`_meta/conventions.md`, all `README.md`
files, the substrate `README.md`, the `log.md` header). Never
overwrite user-authored content.

If `<location>/` exists with content but no
`<location>/_meta/conventions.md`: **refuse.**

> Found existing content at `<location>/` that doesn't look like a
> substrate (no `_meta/conventions.md`). This skill only initializes
> fresh layouts. Three options:
>
> 1. Move the existing content aside, then re-run.
> 2. Wait for `/project-migrate` (planned) to ingest legacy docs.
> 3. Re-run with a different location, e.g. `/project-init project-new/`.

### 3. Plan the writes

Enumerate the changes you'll make. For a fresh init at
`<location>/`:

- 14 new files under `<location>/` (see Templates).
- One edit to (or creation of) `<repo>/CLAUDE.md` — append the
  substrate-aware section.
- One edit to `<repo>/.gitignore` — add the proposals sidecar line if
  not already ignored.

Show the user the plan before writing. Specifically show the
`CLAUDE.md` and `.gitignore` diffs (these are the only existing-file
edits). The 14 new substrate files don't need individual previews —
list them as a tree.

Get explicit go-ahead before writing.

### 4. Write the layout

Create directories first:

```
<location>/
<location>/_meta/
<location>/rules/
<location>/aspirations/
<location>/learnings/
<location>/plans/
<location>/plans/active/
<location>/plans/archive/
<location>/tasks/
<location>/concepts/
<location>/reference/
```

Then write the files per the Templates section. Use today's date for
any `INIT_DATE` placeholders (`date -u +%Y-%m-%d`).

Apply the `CLAUDE.md` and `.gitignore` edits.

### 5. Print summary

Single short message: what was created (one-line tree), what's next.
Example:

> Substrate scaffolded at `project/`. Layout: rules / aspirations /
> learnings / plans (active+archive) / tasks / concepts / reference,
> plus log.md and _meta/conventions.md.
>
> Next:
> - Drop a first decision in `project/rules/<topic>.md` to start
>   populating.
> - When you have content across multiple kinds, run `/project-sync`
>   (planned) to generate concept pages.
> - Set up nightly sweeps with `/schedule` once the substrate has
>   enough content for lint to find anything.

## Templates

Each template is a code block tagged with its target path relative
to `<location>/`, except where noted. Substitute `INIT_DATE` with
today's date (UTC, ISO).

### `README.md` (substrate root)

````markdown
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
- `plans/active/` — what's being worked on now.
- `plans/archive/` — historical and shelved plans.
- `tasks/` — task tracking.
- `concepts/<topic>.md` — auto-generated synthesis pages.
- `reference/` — external-system references.
- `log.md` — append-only chronological history.

## Trust model

- **You (human)** and **foreground Claude** — full read/write here.
- **imp (background)** — read-only. Produces proposals at
  `<repo>.project-proposals/` for review and approval.

See `_meta/conventions.md` for the auto-approval gradient and the
proposal categories.
````

### `log.md`

````markdown
# Substrate log

Append-only chronological record of substrate changes and imp sweep
findings. Entries lead with `## [date] kind | title` so simple grep
works (`grep "^## \[" log.md | tail -20`).

---

## [INIT_DATE] init | substrate created

Substrate initialized via `/project-init`. Layout: rules,
aspirations, learnings, plans (active + archive), tasks, concepts
(auto-gen), reference, log.
````

### `_meta/conventions.md`

````markdown
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
there, not a guarantee one is there now, but useful to carry forward.

Lifecycle: aging. Old learnings fade in surfaced views but don't go
false.

### `plan`
Designs in various states. The `state:` frontmatter field tracks
which:

- `active` — being worked on
- `shelved` — researched but not picked up
- `historical` — shipped, kept for "yeah but why"
- `abandoned` — tried, didn't work, kept for the lesson

Lifecycle: state-tracked. Active plans transition to archive as work
concludes.

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
| `plan` | `state: active \| shelved \| historical \| abandoned`, `supersedes` (list of plan filenames) |
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
- **Plan vs. code** (active plan): tracking. Drift = "not done yet."
- **Plan vs. code** (archived/historical): noise. Suppress.
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
proposals: ../<repo>.project-proposals/  # imp's proposal sidecar (gitignored)
tasks_path: project/tasks/               # or root TODO.md if you prefer flat
```

Defaults are sensible; missing config is fine.
````

### `rules/README.md`

````markdown
# Rules

Locked-in constraints. Things that, if broken, mean either there's a
bug (in code) or the rule needs deliberate revision (by us). Drift
between rules and code is an **alarm**, not a gentle suggestion.

Examples: file format specs, API contracts, design system invariants,
"never do X" prohibitions, shapes that other code depends on.

If you're unsure whether something is a rule or an aspiration: a rule
has a clear right-or-wrong test against code. An aspiration is
something we're working toward and may fall short of.

## Example shape

```yaml
---
kind: rule
title: <short rule title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human
enforces:
  - <file glob this rule constrains>
---

# <Rule title>

<One-sentence statement of the rule.>

## Why
- <reason>
- <reason>

## How violations look
- <example of code that would violate this rule, if useful>
```

See `../_meta/conventions.md` for the full frontmatter spec and drift
semantics.
````

### `aspirations/README.md`

````markdown
# Aspirations

What we're going for. Design intent. Code falls short by definition.
Internal contradictions are normal — they describe the design tension
space ("combat should be deep but intuitive").

Drift between aspiration and code is **informative**, not alarming.
Surfaces on the relevant concept page as the gap analysis.

Examples: tone-of-voice docs, "we want this to feel like X" notes,
philosophical principles, target experience.

## Example shape

```yaml
---
kind: aspiration
title: <short aspiration title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human
---

# <Aspiration title>

<What we're going for, in plain prose. Don't pretend this is a
contract; it's the direction.>

## Tensions
<If this aspiration includes deliberate contradictions, name them
here. They're features, not bugs.>
```

See `../_meta/conventions.md` for full conventions.
````

### `learnings/README.md`

````markdown
# Learnings

Discovered knowledge. Playtest feedback, simulation results, things
read in articles, things noticed during prior implementation, "huh
that didn't work" outcomes.

Learnings are not rules — they're awareness signals. They decay in
*relevance* (the area they touch may get rewritten), not in *truth*
(what was learned was true at the time). Old learnings still
matter for the "yeah but why" view; recent learnings shape current
caution.

The frame: "There's a snake in the back yard." Not a rule that you
want a snake there. Not a guarantee one's there now. But you'd be
careful walking through.

## Example shape

```yaml
---
kind: learning
title: <short learning title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human | imp-research:R-NNN | imp-build:R-NNN
relevance_horizon: YYYY-MM-DD       # optional — date past which to fade
topics: [<topic-slug>, ...]
---

# <Learning title>

<What was learned. Cite the source — playtest session, sim run,
article, prior commit, etc.>

## Implications
<What this means for current or future work, if anything.>
```

See `../_meta/conventions.md` for full conventions.
````

### `plans/active/README.md`

````markdown
# Active plans

Plans currently being worked on. These are the things that, if you
asked "what's in flight right now?", you'd point to.

Frontmatter `state: active`. When work concludes, plans transition to
`../archive/` (state becomes `historical` or `abandoned`).

## Example shape

```yaml
---
kind: plan
title: <plan title>
state: active
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human
---

# <Plan title>

## Goal
<What we're trying to achieve.>

## Approach
<How.>

## Phases / milestones
- [ ] <step>
- [ ] <step>

## Open questions
- <question>
```

See `../../_meta/conventions.md` for full conventions.
````

### `plans/archive/README.md`

````markdown
# Archived plans

Plans that have concluded — either shipped (`state: historical`),
abandoned (`state: abandoned`), or shelved (`state: shelved`,
researched but not picked up).

Archive entries are useful for "yeah but why?" but are not
authoritative on current behavior. The wiki / lint should not flag
drift between archived plans and current code as alarming.

When a plan moves here, consider also producing a learning entry
that captures the lesson — the plan archive preserves the doc; the
learning extracts what we'd carry forward.

## Frontmatter

Same as active plans, but with `state: historical | abandoned |
shelved`.

See `../../_meta/conventions.md` for full conventions.
````

### `tasks/README.md`

````markdown
# Tasks

Tracking items: TODOs, fix-its, in-flight work that doesn't warrant
its own plan yet.

Tasks may be promoted to plans when they grow substantive scope (the
sweep imp will flag this). Until then, they live here as
small entries.

If you prefer a flat root `TODO.md` instead of per-topic task files,
set `tasks_path: TODO.md` in `../_meta/config.yaml`.

## Example shape

```yaml
---
kind: task
title: <short task title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human
topic: <topic-slug>          # optional
parent_plan: <plan-filename> # optional
---

# <Task title>

- [ ] <item>
- [ ] <item>
```

See `../_meta/conventions.md` for full conventions.
````

### `concepts/README.md`

````markdown
# Concepts (auto-generated)

**Do not hand-edit files in this directory.** They are auto-generated
by `/project-sync` and your changes will be overwritten on next run.

Each `<topic>.md` is a synthesis page that pulls from rules,
aspirations, learnings, plans, code state, and tasks for the topic,
and presents them in a unified view with drift analysis.

If a topic doesn't have a concept page yet, run `/project-sync` once
the substrate has enough content. Topics are discovered from the
substrate (entries with overlapping content) plus an optional
`_topics.yaml` seed list.

See `../_meta/conventions.md` for the concept page schema.
````

### `reference/README.md`

````markdown
# Reference

Pointers to external systems — build instructions, tool READMEs,
third-party docs, configuration of services we depend on.

Truth lives in the doc; the substrate lints for drift between the
doc's claims and the actual scripts / configs.

## Example shape

```yaml
---
kind: reference
title: <short title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human
subject: <what external thing this references — e.g. "Azure deploy pipeline">
---

# <Reference title>

<Plain description of the external thing, what we use it for, and
where to find it.>

## Where to find it
- <link or path>

## Local interaction
- <commands, configs that touch this>
```

See `../_meta/conventions.md` for full conventions.
````

### `_meta/config.yaml`

````yaml
# Project substrate configuration. All keys optional; defaults shown.
# Uncomment and edit as needed.

# location: project/                      # substrate location, relative to repo root
# proposals: ../<repo>.project-proposals/ # imp proposal sidecar (gitignored)
# tasks_path: project/tasks/              # or "TODO.md" for flat root tracking
````

### `<repo>/CLAUDE.md` snippet (append, or create if missing)

If `CLAUDE.md` exists, append the snippet below as a new section. If
the section already exists (detect by the `## Project substrate`
heading), no-op. If `CLAUDE.md` doesn't exist, create it with this as
the first section.

````markdown
## Project substrate

This repo uses a structured project-knowledge substrate at `<location>/`.
Read from it before answering questions about design, intent, or
current behavior. You may edit substrate files directly when
capturing decisions, learnings, or plans. imp may not — it produces
proposals you (or the human) review and approve.

### What's where

- **`<location>/concepts/<topic>.md`** — auto-generated synthesis
  pages. Start here for a topic overview. Don't hand-edit;
  regenerated by `/project-sync`.
- **`<location>/rules/`** — locked-in constraints. Code violating
  these is a bug. Internal contradictions are alarming.
- **`<location>/aspirations/`** — what we're going for. Contradictions
  are fine; code falls short by definition.
- **`<location>/learnings/`** — discovered knowledge. Decays in
  relevance, not in truth.
- **`<location>/plans/active/`** — what's being worked on now.
- **`<location>/plans/archive/`** — historical and shelved plans.
- **`<location>/tasks/`** — task tracking.
- **`<location>/log.md`** — append-only chronological history.

For drift semantics per kind, see `<location>/_meta/conventions.md`.

### imp proposals

imp produces proposals at `<repo>.project-proposals/P-NNN-<slug>.md`
when scheduled sweeps detect promotion candidates, drift, doc rot,
etc. Auto-approval gradient when reviewing on the user's behalf:

- **Always-safe** (auto-apply): `log.md` appends, archive moves.
- **Claude-approvable**: new learning entries, concept regeneration,
  candidate flags.
- **Human-required**: rules edits, deletions, supersede markers,
  anything that loses information.
````

### `<repo>/.gitignore` line (add if not present)

```
<repo>.project-proposals/
```

Replace `<repo>` literally with the repo's directory name (basename
of the repo root). Example: if the repo is at `~/repos/dreamlands`,
the line is `dreamlands.project-proposals/`.

If the line is already present, no-op.

## Edge cases

- **`<location>/_meta/conventions.md` exists, but other files don't.**
  Treat as "partial init from a previous interrupted run." Resume:
  write only the missing files. Don't overwrite the existing
  conventions.
- **`CLAUDE.md` exists with a `## Project substrate` heading already.**
  No-op on the snippet. Print: "Substrate section already in
  CLAUDE.md — leaving alone."
- **`.gitignore` doesn't exist.** Create it with just the proposals
  line.
- **User typed an absolute path for the location.** Refuse — the
  substrate must live inside the repo. Suggest a relative path.
- **Repo has uncommitted changes elsewhere.** Don't refuse, but
  mention it in the summary so the user knows their substrate-init
  commit will be mixed with other changes if they `git add .`.

## Notes for future iterations

- **Idempotent re-run** with `--force` should be conservative: never
  touch user content. The skill-owned files are
  `_meta/conventions.md`, all `README.md` files inside the substrate
  dirs, the substrate `README.md`, and the `log.md` header.
  Everything else is user content.
- **`tasks_path: TODO.md`** alternative: when set, the skill creates
  a `TODO.md` at the repo root instead of `tasks/` content, with the
  same example shape inline as a comment block. The `tasks/` dir
  still exists (empty, with README) for users who later switch to
  per-topic tasks.
- **Seed an aspiration from README:** the spec mentions an optional
  interactive step ("I see a README. Want me to seed an aspirations
  entry from it?"). Skipping in v0.1 — keeping init minimal. Add
  later if user wants.
