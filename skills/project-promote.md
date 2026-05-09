---
name: project-promote
description: Use when reviewing and applying imp-generated proposals at <repo>.project-proposals/. Implements the trust-model gate between imp's read-only output and the substrate. Categorizes proposals by approval tier (always-safe / claude-approvable / human-required), shows rationale and previews, applies changes atomically. Default behavior with no args is to list pending proposals; takes a specific proposal id to review or --batch to walk all pending.
---

# /project-promote

Reviews imp-generated proposals at `<repo>.project-proposals/` and
applies them to the substrate at `project/` after appropriate
approval. Closes the H8/H9 trust loop: imp proposes, promote applies,
substrate stays auditable.

Design context lives in `<repo>/project/project-promote-spec.md` and
`<repo>/project/project-substrate-notes.md` (H6, H8, H9, H10) if you
need it. The procedure below is self-contained.

## When to use

- User asks to review imp's proposals, apply pending changes, or
  approve recent imp work.
- User invokes the skill with `/project-promote` (with or without
  arguments).
- A scheduled routine wants to apply queued proposals (autonomous
  invocation — see Tier handling).

When **not** to use:

- The substrate doesn't exist yet → direct user to `/project-init`.
- Outside a git repo → refuse.
- Substrate has uncommitted changes (would break atomic rollback) →
  refuse, ask user to commit substrate state first.

## Procedure

### 1. Locate substrate and proposals

Find the substrate location:
- Default: `<repo-root>/project/`. Get repo root via
  `git rev-parse --show-toplevel`.
- Override: read `project/_meta/config.yaml` `location:` if present.

Find the proposals directory:
- Default: `<dirname-of-repo>/<basename-of-repo>.project-proposals/`.
- Override: `_meta/config.yaml` `proposals:` if present.

If the substrate doesn't exist or doesn't have `_meta/conventions.md`:
refuse with "no substrate found at `<location>` — run /project-init
first."

If the proposals directory doesn't exist or has no `P-*.md` files:
print "no pending proposals." Exit cleanly.

### 2. Atomicity precondition

Before doing anything else, verify `git status --short project/`
shows no modifications. If there are any uncommitted substrate
changes (staged or unstaged): refuse with "substrate has uncommitted
changes — commit first so promote has a clean rollback target."

Reasoning: promote applies changes by editing files in place. The
rollback strategy is `git checkout project/`. That only works if
the substrate is committed. Forcing the precondition keeps the
implementation simple.

### 3. List pending proposals

Glob `<proposals-dir>/P-*.md` (excluding `_applied/`). For each, read
frontmatter and extract:
- `proposal_id`
- `generated_at`
- `generated_by`
- `category`
- `status` (must be `pending` to consider)

Skip any with `status: applied` or `status: rejected`.

For each pending proposal, also infer the tier (see Tier rules below).

If the user invoked with no args: print a table:

```
Pending proposals:

  P-2026-05-09-001  promotion          claude-approvable    "Combat plan shipped..."
  P-2026-05-09-002  drift_alarm        always-safe          "Code in lib/X violates..."
  P-2026-05-09-003  learning_candidate claude-approvable    "Build R-NNN noted..."

Run /project-promote <id> to review one, or /project-promote --batch
to walk through all of them.
```

If the user invoked with `--auto-safe`: apply all always-safe
proposals without prompting (auto-approval), then print the remaining
pending list.

If the user invoked with a specific id (e.g. `P-2026-05-09-001`):
proceed to step 4 with just that proposal.

If `--batch`: proceed through all pending proposals one at a time.

### 4. Display proposal

For each proposal under consideration:

Read the full proposal file. Display:

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Proposal: P-2026-05-09-001
Category: promotion
Tier: claude-approvable
Generated: 2026-05-09T14:30:00Z by imp-research:R-NNN

# <Title>

## Rationale
<full rationale section, rendered as markdown>

## Proposed changes
- move plans/active/x.md → plans/archive/x.md (state: shipped)
- create rules/new-thing.md (preview-1)
- append project/log.md
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

If there are previews referenced in the changes block (`preview:`
keys), show them. For long previews, show the first 30 lines and
note the rest is in the proposal file.

### 5. Get approval per tier

The tier determines who decides:

**Always-safe**:
- Auto-apply when invoked with `--apply`, `--batch`, or `--auto-safe`.
- Otherwise prompt: "Apply / skip / reject? [a]"  Default `apply`.

**Claude-approvable**:
- When invoked from a scheduled/autonomous context (no human present
  signal): apply if substrate state and proposal coherence look
  reasonable; skip otherwise.
- When invoked interactively (default): always prompt the human.
  "Apply / skip / reject? [s]"  Default `skip` (conservative).
- When invoked with `--apply`: apply without re-prompting.

**Human-required**:
- Never auto-apply. Always show and prompt.
- `--apply` does *not* override this.
- If invoked from autonomous context: show the proposal, log "human
  review needed for P-NNN," do not apply.
- Interactive prompt: "Apply / skip / reject? [s]"  Default `skip`.

For now (v0.1): treat every invocation as interactive. Autonomous
invocation lands when scheduled routines start calling promote.

If the user picks `reject`: ask for an optional reason. Update the
proposal frontmatter `status: rejected` and `rejected_at: <date>`,
then move the proposal to `<proposals-dir>/_rejected/P-NNN-*.md`.
Skip the apply step.

If the user picks `skip`: leave the proposal pending; move to the
next one (or exit if not in batch mode).

If the user picks `apply`: proceed to step 6.

### 6. Apply changes

For each change in the proposal's `## Proposed changes` YAML block,
execute it. Order matters: do `move` operations before `create`
operations that would write to overlapping paths, do `append`
operations last (idempotent if re-run).

**Change types and how to apply each:**

#### `create`
```yaml
- type: create
  path: project/rules/new-thing.md
  preview: preview-1     # references a "## Preview: preview-1" section in the proposal
```

- Resolve the preview content from the named `## Preview: <name>`
  section in the proposal file.
- Refuse if path already exists. (If the proposal needed to overwrite,
  it should use `edit` with explicit content replacement, which is
  human-required.)
- Use the Write tool to create the file.

#### `move`
```yaml
- type: move
  from: project/plans/active/x.md
  to: project/plans/archive/x.md
  set_frontmatter:           # optional
    state: shipped
    updated: 2026-05-09
```

- Use Bash `mv` to move the file.
- If the source has a companion directory (e.g. `from` is
  `plans/active/x.md` and `plans/active/x/` exists), move the dir
  too: `mv project/plans/active/x project/plans/archive/x`.
- If `set_frontmatter:` is present, update those keys in the new
  file's YAML frontmatter via Edit. **Read the file at its new path
  before Edit** — the Edit tool requires a prior Read of the exact
  path you're editing. Without this you'll get "File has not been
  read yet" and the apply step will fail mid-flight.

#### `append`
```yaml
- type: append
  path: project/log.md
  content: |
    ## [2026-05-09] promotion | x shipped
    Promoted from imp-research R-2026-05-09-002.
```

- Read the target file, append the content (with a leading newline
  if the file doesn't end in one), write back via Edit (old_string =
  last few lines, new_string = last few lines + appended content).

#### `set_frontmatter`
```yaml
- type: set_frontmatter
  path: project/plans/active/x.md
  set:
    state: active
    updated: 2026-05-09
```

- Read the file, find the YAML frontmatter block, update the named
  keys (preserving other keys), write back via Edit.

#### `edit` (v0.1: append-section only)
```yaml
- type: edit
  path: project/rules/foo.md
  edit_kind: append_section
  section_heading: "## Open questions"   # if absent, append to end of body
  content: |
    - new question added by imp
```

- v0.1 limitation: only `append_section` is supported. Adding text to
  existing sections is allowed; replacing or removing existing text
  is not. Anything more invasive should be human-required and the
  proposal should use `create` (new file) plus a separate
  human-required `delete` proposal.
- If `section_heading` is given: find that heading in the file,
  append `content` to that section (before the next heading at same
  or higher level).
- If `section_heading` is absent: append at end of body (before any
  trailing whitespace).

#### `delete`
- Tier: always human-required.
- Use Bash `rm`. Companion dirs are NOT auto-deleted; if a plan's
  `.md` is being deleted (rare), the proposal must explicitly
  include a separate `delete` for the companion dir.

**On any failure**: do not continue to the next change. Roll back via
`git checkout project/` (which restores all substrate files to the
last commit) and report the failure to the user. The proposal stays
`pending`.

### 7. Update proposal status

After all changes apply successfully:

- Update the proposal's frontmatter:
  - `status: applied`
  - `applied_at: <today's date>`
  - `applied_by: human` (or `claude` if Claude auto-applied)
- Move the proposal file to
  `<proposals-dir>/_applied/P-NNN-<slug>.md`. Create `_applied/` if
  it doesn't exist.

### 8. Log

Append an entry to `project/log.md` (as a substrate write — this is
itself a substrate change but it's `always-safe` so don't recurse):

```
## [YYYY-MM-DD] promote | applied P-NNN

Category: <category>. Source: <generated_by>.
Affected: <list of paths touched>.
```

(Use kind `promote` here — the audit-trail entry recording that
`/project-promote` applied a proposal. Don't use `promotion` as the
kind, since "promotion" is also a proposal category name and the
collision is confusing in `log.md`.)

### 9. Print summary

Single short message: which proposals were applied, skipped, or
rejected. Pointer to log. If batch mode, summary across all.

## Tier inference rules

For each change in the proposal's changes block, determine its tier.
The proposal's overall tier is the *highest* tier across all its
changes.

| Change | Target | Tier |
|---|---|---|
| `append` | `project/log.md` | always-safe |
| `append` | `project/_drift.md` (when it exists) | always-safe |
| `move` | from `plans/active/` to `plans/archive/`, with at most a `state:` set_frontmatter | claude-approvable |
| `create` | in `learnings/`, `tasks/`, or `plans/active/` (state: exploring) | claude-approvable |
| `set_frontmatter` | any plan file, only `state` / `updated` keys | claude-approvable |
| `edit` (append_section) | in `learnings/`, `tasks/`, `plans/`, `concepts/` | claude-approvable |
| `create` | in `rules/` | **human-required** |
| `create` | in `aspirations/` | **human-required** |
| `create` | in `reference/` | **human-required** |
| `edit` | in `rules/` or `aspirations/` (any kind) | **human-required** |
| `set_frontmatter` | in `rules/` or `aspirations/` | **human-required** |
| `delete` | any path | **human-required** |
| Anything touching `_meta/conventions.md` or `_meta/config.yaml` | | **human-required** |
| Anything touching `concepts/` (other than appends) | | **human-required** (route to `/project-sync` instead) |

If any change matches no rule above, default to **human-required**.
Conservative-by-default.

## Atomicity / safety

- Precondition: substrate must be git-clean (no uncommitted changes
  in `project/`). Refuse if not.
- Apply changes in order; on first failure, `git checkout project/`
  to restore.
- The proposal status update (step 7) and log append (step 8) happen
  *after* successful apply. If they fail, the substrate change is
  already committed-shaped (clean tree mutated to a new clean state)
  but the proposal hasn't been marked applied. Re-running promote
  will detect the substrate matches the proposal's intended state
  and skip — see "Idempotency" below.
- Never write outside `project/` or `<proposals-dir>/`.

## Idempotency

A proposal whose substrate effects are already present (e.g. the
target file already exists with the right content) is detected by:
- `create` change: target exists with content matching the preview →
  no-op for that change.
- `move` change: source doesn't exist but target does → no-op.
- `append` change: file already contains the appended content
  (verbatim match) → no-op.
- `set_frontmatter`: target keys already have the requested values →
  no-op.

If all changes are no-ops, the proposal can be marked `applied`
without re-applying. This handles the case where promote was
interrupted between substrate apply (success) and proposal-status
update (incomplete).

## Edge cases

- **Stale proposal**: re-validate file references in the proposal
  against current substrate before applying. If a referenced source
  file no longer exists or has changed materially: refuse with
  "proposal P-NNN is stale relative to current substrate state. Ask
  imp to regenerate." Don't try to repair.
- **Conflicting proposals** (two pending want same target file): when
  iterating in `--batch`, detect via target-path overlap. Apply the
  first, then re-validate the second; if it's now stale, skip with a
  message.
- **Malformed YAML changes block**: refuse, log the parse error,
  leave proposal `pending`. Print the error so the user can ask imp
  to regenerate.
- **Empty proposals dir**: friendly "nothing pending" exit.
- **Proposal references a preview that doesn't exist** (e.g.
  `preview: preview-99` but no `## Preview: preview-99` section):
  refuse the proposal, mark malformed.
- **Substrate has companion dir for a plan in `move`**: move both
  together. Test for the companion dir's existence first.
- **Sub-second double invocation**: not handled by promote (the
  user shouldn't invoke twice in parallel). If it happens, one of
  the runs will fail the git-clean precondition.

## Configuration

Read from `project/_meta/config.yaml`:

```yaml
# Optional
auto_safe: false       # if true, auto-apply always-safe proposals on /project-promote with no args
batch_default: false   # if true, default to --batch behavior on no-arg invocation
```

Defaults conservative.

## Notes for future iterations

- **Real `edit` (replace section)**: v0.1 only supports
  `append_section`. Replacing existing text is human-required and
  the proposal should currently work around it via `create` + manual
  delete. Add full replace_section once we see real proposals
  needing it.
- **Concept page handling**: if a proposal touches `concepts/`
  beyond appends, refuse and route to `/project-sync` (not yet
  built). For v0.1 we don't expect this.
- **Auto-batch always-safe**: `--auto-safe` flag does this. Could
  default to true once trust is established.
- **Autonomous invocation** (Claude on the human's behalf, not
  interactive): the tier logic is specced but defaults to
  interactive in v0.1. Implement the autonomous path when scheduled
  routines start calling promote.
- **Retention**: `_applied/` and `_rejected/` accumulate. Add a
  retention policy ("prune entries older than 90 days") when volume
  warrants.
