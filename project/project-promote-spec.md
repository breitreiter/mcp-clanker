# `/imp-promote` — Spec

> The skill that applies imp proposals to the substrate. The
> human-or-Claude-mediated gate between imp's read-only output and
> the substrate's authoritative state. Implements the trust model
> from substrate notes H8 and the proposal-as-output-contract from
> H9.

## Purpose

imp is read-only against the substrate. It produces proposals at
`<repo>.imp-proposals/P-NNN-<slug>.md` describing changes it
suggests — promotions, demotions, drift alarms, learning candidates,
TODO promotions, etc. (See substrate notes H9 for the full category
set.)

`/imp-promote` is the skill that *reads those proposals, gets
approval at the appropriate tier, and applies the changes*. Without
it, proposals accumulate but never affect the substrate. With it,
imp's overnight sweeps become useful work.

## Scope

**Does:**
- Lists pending proposals with summary info.
- Reads a specific proposal, displays rationale + proposed changes,
  asks for approval at the appropriate tier.
- Applies approved changes (file creates, moves, appends, edits).
- Updates the proposal's `status:` frontmatter (`pending → applied |
  rejected`).
- Appends a log entry to `project/log.md` for every applied proposal.
- Auto-applies always-safe changes without prompting (configurable —
  off by default for first-time users).

**Does not:**
- Generate proposals — that's imp's job.
- Modify imp's private scratch.
- Run sweeps or any other long-running work.
- Edit code outside the substrate.

## Invocation

```
/imp-promote                    # list pending proposals
/imp-promote P-NNN              # review and act on one proposal
/imp-promote P-NNN --apply      # apply without re-asking (claude-approvable+ only)
/imp-promote --batch            # walk through all pending proposals one by one
/imp-promote --auto-safe        # auto-apply all always-safe proposals (no prompts)
```

Default behavior with no args: print the pending list and stop. Don't
auto-act. Approval is always explicit on a fresh invocation; flags
are how the user opts into batch behavior.

## Procedure

### 1. Locate proposals

Default location: `<repo-parent>/<repo-basename>.imp-proposals/`.
Configurable via `project/_meta/config.yaml` `proposals:` key.

If the directory doesn't exist or is empty: print "no pending
proposals" and exit.

### 2. List or pick

For each `P-NNN-<slug>.md` with frontmatter `status: pending`,
extract:
- `proposal_id`
- `generated_at`
- `generated_by` (imp run id)
- `category` (promotion / demotion / drift alarm / etc.)
- `tier` (always-safe / claude-approvable / human-required) — see
  inference rules below
- The first line of the rationale (one-line summary)

Print as a table. If user invoked with a specific `P-NNN`, skip
listing and jump to the proposal. If `--batch`, walk them in order.

### 3. Read and display the proposal

For each proposal, show:
- Header: id, category, generated_at, generated_by, tier
- Full rationale section (markdown narrative)
- Summary of proposed changes (one line each)
- Previews of any new content (fenced as in the proposal)

Then prompt for the appropriate tier:

- **Always-safe**: confirm with "[apply / skip / reject]". Default
  apply on enter.
- **Claude-approvable**: when invoked by Claude on the human's
  behalf, Claude makes the decision based on substrate state and
  apply if reasonable. When the human is present, prompt with
  "[apply / skip / reject / human-decide]".
- **Human-required**: prompt with "[apply / skip / reject]" and
  *never* let Claude auto-apply, even with `--apply`. If invoked by
  Claude alone, surface the proposal and stop.

### 4. Apply

For each change in the proposal's `changes` block, execute it:

- `create` → write the file (refuse if path exists; the proposal
  must use `edit` for that case)
- `move` → mv the source to the target; preserve the companion
  directory if the source is a plan with one
- `append` → append content to the target file (must end in newline)
- `edit` → replace a specific section by heading match, or apply a
  structured edit (insert/remove section by heading)
- `set_frontmatter` → update specific frontmatter keys on the target
  file
- `delete` → remove the file (only as part of a `move` that's a
  rename-to-archive, or with explicit human approval)

If any change fails (path missing, edit collision, etc.): roll back
the partial-apply and report. The substrate must never be left in a
half-applied state.

### 5. Mark applied

Update the proposal's frontmatter: `status: pending → applied`,
`applied_at: <date>`, `applied_by: human | claude`. Move the proposal
file to `<proposals-dir>/_applied/P-NNN-<slug>.md` (sidecar archive
of past proposals — not in the substrate).

### 6. Log

Append to `project/log.md`:

```
## [YYYY-MM-DD] promote | applied P-NNN

Category: <category>. Source: <generated_by>.
Affected: <list of paths>.
```

(Kind is `promote` — the audit-trail entry. Avoid `promotion` here:
that collides with one of the proposal *category* names.)

## Proposal file format

What `/imp-promote` accepts. This is the contract imp must honor
when generating proposals.

```markdown
---
proposal_id: P-2026-05-09-001
generated_at: 2026-05-09T14:30:00Z
generated_by: imp-research:R-2026-05-09-002
category: promotion | demotion | drift_alarm | doc_rot | learning_candidate | concept_staleness | todo_promotion
status: pending
---

# Proposal: <one-line title>

## Rationale

<Human-readable narrative. Why this proposal exists. What signal
triggered it. Cite specific files, line numbers, code references.
This is what humans read to decide.>

## Proposed changes

```yaml
changes:
  - type: move
    from: project/plans/active/x.md
    to: project/plans/archive/x.md
    set_frontmatter:
      state: shipped
      updated: 2026-05-09
  - type: create
    path: project/rules/new-thing.md
    preview: preview-1
  - type: append
    path: project/log.md
    content: |
      ## [2026-05-09] promotion | x shipped
      Promoted from imp-research R-2026-05-09-002.
```

## Preview: preview-1

```yaml
---
kind: rule
title: New thing
created: 2026-05-09
updated: 2026-05-09
provenance:
  source: imp-research:R-2026-05-09-002
  promoted_at: 2026-05-09
  promoted_by: claude
enforces:
  - lib/Foo/**
---

# New thing

<rule body>
```

## How to apply

`/imp-promote P-2026-05-09-001`
```

The structure:
- Frontmatter: id, timestamps, source, category, status.
- `## Rationale` — required, narrative for human review.
- `## Proposed changes` — required, YAML block with the change list.
- `## Preview: <name>` sections — optional, used by `create` changes
  that reference a `preview:` key. Each preview is a fenced
  code block containing the file content.
- `## How to apply` — informational, repeats the invocation.

## Auto-approval tier inference

Per H8, three tiers. Inferred from the change list:

| Change pattern | Tier |
|---|---|
| append to `log.md` only | always-safe |
| append to `_drift.md` only | always-safe |
| move plan from `active/` to `archive/` (no content edit beyond `state:`) | claude-approvable |
| create new file in `learnings/` | claude-approvable |
| create new file in `tasks/` | claude-approvable |
| create new file in `plans/active/` (state: exploring) | claude-approvable |
| create new file in `rules/` | **human-required** |
| create new file in `aspirations/` | **human-required** |
| edit existing file in any kind dir (other than frontmatter-only) | **human-required** |
| `delete` change | **human-required** |
| supersede marker added to existing file | **human-required** |
| any change touching `_meta/conventions.md` or `config.yaml` | **human-required** |

The proposal's overall tier is the *highest* tier of any change in
its list. A proposal that creates a learning *and* edits a rule is
human-required as a whole.

Edge cases (open):
- A proposal that adds a section to an existing rule (additive, not
  replacing wording) — currently human-required, but feels
  claude-approvable. Defer until we see real ones.
- Concept-page regeneration (touches `concepts/<topic>.md`) — these
  are auto-generated by `/project-sync`, so `/imp-promote`
  shouldn't see them in proposals. If it does, route to
  `/project-sync` instead.

## Idempotency and safety

- A proposal with `status: applied` is no-op'd if seen again.
- A proposal with `status: rejected` is no-op'd unless `--reapply`.
- Applying a proposal is atomic: all changes succeed, or the
  substrate is rolled back. (Implementation: write to a temp dir,
  move into place on success, or use git to mark a checkpoint
  before applying.)
- Never auto-apply across substrate boundaries (e.g. a proposal
  that touches both code and substrate is rejected as
  out-of-scope; promote only writes to the substrate).
- The skill never writes to imp's private scratch or to code paths.
  Only substrate paths and the proposals sidecar.

## Edge cases

- **Stale proposal**: substrate state has changed since the proposal
  was generated (e.g. the rule it wanted to promote has been
  hand-edited). Detection: re-validate references in the proposal
  against current substrate. If references are stale, refuse to
  apply and surface to the human with "this proposal is stale; ask
  imp to re-generate."
- **Conflicting proposals**: two pending proposals both want to
  modify the same file. Detection: check before applying. Refuse
  the second; let the human decide order.
- **Proposal with malformed YAML changes block**: refuse, log the
  parse error, leave proposal as `pending`. Don't try to fix.
- **Empty proposal directory**: print friendly "nothing pending"
  message and exit.
- **Proposal references a file that no longer exists**: stale; same
  handling as above.

## Configuration

Optional fields in `project/_meta/config.yaml`:

```yaml
# proposals: ../<repo>.imp-proposals/   # already declared elsewhere
auto_safe: false                              # auto-apply always-safe proposals on /imp-promote with no args
batch_default: false                          # default to --batch behavior on no-arg invocation
```

Defaults conservative — explicit approval on every invocation.

## Open decisions (during build)

- **Sidecar archive of applied proposals** at `_applied/` vs. just
  deleting them. Argument for archiving: provenance / audit.
  Argument for deleting: noise. Lean toward archiving with a
  retention policy ("keep last 90 days"). Defer until we see real
  volume.
- **Preview block format**: keep the `preview:` reference in the
  changes block + named `## Preview: <name>` sections (current
  spec) vs. inlining content directly in the changes block (uglier
  YAML but no resolution step). Current shape is more
  human-readable; defer changing.
- **Atomicity implementation**: temp-dir-then-move vs. git-checkpoint.
  Git-checkpoint is simpler if we're willing to assume the user
  will commit substrate changes. Temp-dir-then-move handles
  uncommitted state better. Defer until first real failure case.
- **Edit changes**: how granular? Replace-section-by-heading is
  reasonable for v0.1; finer-grained line edits are a feature
  request when needed.

## Build order

1. Implement proposal listing (parse frontmatter from all
   `P-*.md` files in the proposals dir).
2. Implement proposal display (render rationale + changes summary).
3. Implement tier inference from changes list.
4. Implement change application for each change type, one at a time:
   `append` first (smallest blast radius), then `create`, `move`,
   `set_frontmatter`, `edit`, `delete` (most cautious).
5. Implement atomicity (temp-dir staging or git checkpoint —
   pick one).
6. Implement status update on the proposal file.
7. Implement log append.
8. Manual test against synthetic proposals (write a fake
   `P-2026-05-09-001.md` by hand, walk through promote on it).
9. Test against an imp-generated proposal once a sweep skill
   exists. (Won't have one until later; for v0.1, synthetic is
   enough.)

## Done when

- A pending proposal can be listed, displayed, approved, and
  applied.
- The substrate state after apply matches what the proposal said
  it would do.
- The proposal file is moved to `_applied/` with `status: applied`.
- A log entry exists in `project/log.md`.
- A failed apply leaves the substrate unchanged (rollback works).
- Tier inference correctly routes a learning-only proposal to
  claude-approvable and a rule-edit proposal to human-required.
