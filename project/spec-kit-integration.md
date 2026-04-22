# Integrating with GitHub Spec Kit

An exploratory doc — not a plan. Premise: *if* we adopt Spec Kit
(`github/spec-kit`) as the higher-level orchestration layer, what does
mcp-clanker's role look like, and what would we change?

## Why this pairing is plausible

Spec Kit and mcp-clanker cover complementary gaps.

**Spec Kit's strengths:**
- Workflow scaffolding: `Constitution → Specify → Plan → Tasks → Implement`
- User-story tagging (`[US1]`) and parallelism hints (`[P]`)
- Phase structure (Setup → Foundational → User-story phases → Polish)
- Shipped templates under real adoption

**Spec Kit's documented weaknesses** (from the bucket-a-survey and GitHub issues):
- *"TODO = completed task"* — model marks `- [x]` with only a `// TODO` comment
- No enforcement that implementation follows the task plan
- `/speckit.review` exists but isn't a pipeline stage
- Scott Logic's critique: 33.5 minutes and 2577 lines of markdown to produce 689 lines of code

**mcp-clanker's strengths** (mostly in v2 territory):
- Worktree isolation per task
- Structured proof-of-work (not a checkbox)
- Safety gates (danger-pattern, network-egress, doom-loop)
- Closeout sub-agent for independent verification (Codex reviewer pattern)
- Execution trace for forensic diagnosis

The pairing: Spec Kit decomposes work and tracks user stories; clanker
executes each task safely, independently verified, with a checkbox flip
that actually means something.

## Division of labor (proposed)

| Concern | Owner |
|---|---|
| User stories, acceptance criteria at feature level | Spec Kit (`spec.md`) |
| Architecture, data model, API shape | Spec Kit (`plan.md`) |
| Task decomposition + parallelism hints | Spec Kit (`tasks.md`) |
| Phase ordering (Setup → Foundational → Polish) | Spec Kit |
| Ambient coding standards | Spec Kit (`constitution.md`) + clanker reads it |
| Per-task execution | clanker (`build()`) |
| Per-task verification | clanker (v2 closeout sub-agent) |
| Scope enforcement (no out-of-scope file mutations) | clanker |
| Safety (danger patterns, network egress) | clanker |
| Checkbox flip (`- [ ]` → `- [x]`) | clanker, only on verified success |
| Forensic trace | clanker |
| Worktree management | clanker |

Spec Kit is the coordinator; clanker is the implementor + verifier in
that role-split. ("Coordinator/Implementor/Verifier," which the survey
flagged as less common than architect/editor, becomes natural when the
coordinator is a human-facing template toolkit and the
implementor+verifier is a headless executor.)

## Integration points (concrete changes needed in clanker)

### 1. Bridge: task line → contract

Spec Kit's task line format:

```
- [ ] T-005 [P] [US1] Implement password-reset endpoint in src/api/auth.ts
```

Clanker's contract format expects `Goal:`, `Scope:`, `Contract:`,
`Acceptance:`, `Non-goals:`. Something has to bridge. Three options
roughly in order of ambition:

- **Opus-side skill** synthesizes a contract from the task line + `spec.md`
  + `plan.md` context, then calls `build()`. No changes to clanker.
  Skill lives in the Claude Code project. *Simplest.*
- **New MCP tool**: `compile_contract(tasksFile, taskId) → contract.md`.
  Reads task line, pulls matching user story from `spec.md`, pulls
  relevant plan section, emits a contract. Clanker stays
  orchestrator-agnostic but gains Spec-Kit-aware plumbing.
- **Clanker accepts task lines natively** and synthesizes internally.
  Least flexible; couples clanker to Spec Kit's format.

My lean: option 1 (skill-side) for v1 of integration. Move to option 2
only if we see the synthesis needs to be deterministic/reproducible
rather than model-mediated.

### 2. Constitution as ambient context

`constitution.md` is analogous to `AGENTS.md` — project-wide rules. If
it exists, clanker should prepend it to the system prompt during
`build()`. Implementation is cheap:

```csharp
// In Prompts.LoadSystemPrompt:
var ambient = LookForAmbientContext(targetRepo);  // constitution.md, AGENTS.md, .specify/constitution.md
var prompt = template.Replace("{{AMBIENT}}", ambient ?? "");
```

Needs a `{{AMBIENT}}` token in `Prompts/default.md` and
`Prompts/AzureFoundry.md`. Backward-compatible — missing token renders
as empty string.

### 3. Acceptance linkage

For a task tagged `[US1]`, the acceptance criteria are the
EARS/sub-bullets of User Story 1 in `spec.md`. The bridge layer (option
1 or 2 above) pulls these when synthesizing the contract's
`**Acceptance:**` section. Clanker doesn't need to know about user
stories — it just sees a populated acceptance list.

When v2 closeout sub-agent lands, it'll verify against those acceptance
items. That's the hinge that fixes Spec Kit's "TODO = done" failure
mode: closeout reads the diff, checks each EARS assertion, returns
pass/fail with evidence. Only then does the checkbox flip.

### 4. Checkbox flip on verified success

New MCP tool, proposed signature:

```csharp
[McpServerTool]
mark_task_done(string tasksFile, string taskId, string buildResultJson)
```

Parses the proof-of-work. Flips `- [ ] T-NNN ...` to `- [x]` only if:
- `terminal_state == "success"`
- (v2) `acceptance[]` all `pass`
- `files_changed` is non-empty (defensive — a run that changed nothing probably didn't do the work)

If any check fails, appends a structured annotation to the task line
pointing at the worktree + trace for human review, rather than a silent
skip.

### 5. Phase awareness

Clanker stays oblivious to phases. Spec Kit / the orchestrator decides
when each `T-NNN` runs. A blocked clanker run doesn't try to skip
ahead — it returns, and the coordinator decides whether to re-run,
rescope, or escalate.

## What we deliberately do NOT change

- **Don't adopt Spec Kit's one-line task format as clanker's primary
  contract format.** Lossy for anything multi-file. Contract-format
  richness is one of clanker's value propositions.
- **Don't couple clanker to Spec Kit.** The bridge should be thin. A
  different orchestration layer (user writing contracts by hand, nb
  driving them directly, some future thing) should work without code
  changes in clanker.
- **Don't build parallel execution into clanker in response to `[P]`**.
  Parallelism is the coordinator's concern — it runs `N` worktrees
  concurrently by calling `build()` `N` times. Clanker already
  supports this (one worktree per contract; branch-per-task naming).
- **Don't silently flip checkboxes on terminal=success alone.** The
  whole point of the integration is that clanker's POW is richer
  than a checkbox. Preserve that strictness.

## Impact on the existing v2 plan

Pairing with Spec Kit doesn't move v2 phases around, but it *changes
what phase 2 looks like*:

- **Phase 2 (real contract)**: instead of me+you manually writing a
  medium contract, run `/speckit.specify` + `/speckit.plan` +
  `/speckit.tasks` on a small feature, pick one task, use the skill
  (phase 2a) to synthesize a clanker contract from it, run, observe.
  More realistic and immediately tests the integration surface.
- **Phase 4 (acceptance self-check)**: if we're already pulling
  acceptance from `spec.md` via the bridge, the self-check turn has
  structured acceptance items to evaluate against — makes the "cite
  the specific line" prompt more tractable.
- **Phase 5 (closeout)**: same — closeout reviewer reads the diff
  against Spec Kit's EARS-style acceptance. This is where "TODO = done"
  gets actually prevented.

The phases that don't change: instruments (phase 1), safety+toolkit
(phase 3), Docker (phase 6).

## Open questions to resolve before committing

1. **Does Spec Kit expose programmatic access to `tasks.md` / `spec.md`,
   or is the integration purely file-path-based?** If file-path-based
   (likely), the bridge reads these files directly — no Spec Kit API
   dependency. Good for loose coupling.
2. **How does Spec Kit handle a task that clanker returns `blocked` on?**
   Probably nothing — the checkbox stays unchecked and the user sees
   the POW annotation. Parent (human or Opus) rewrites the task and
   re-runs. Worth confirming there's no state Spec Kit expects to be
   in between checked and unchecked.
3. **Constitution evolution.** If `constitution.md` changes mid-build,
   the worktree's version is stale (checked out at start-of-contract).
   Probably fine — contracts are short-lived — but worth noting for the
   "worktree is at HEAD" gotcha already in `CLAUDE.md`.
4. **Does Spec Kit's `/speckit.tasks` generate enough acceptance
   context to populate contract `Acceptance:` sections without a
   second Opus call?** If not, contract synthesis requires a model
   turn, which costs tokens on the orchestrator side. Measure when we
   try it.
5. **Multi-task coordination.** Spec Kit gives us `[P]` parallelism
   hints and `[US1]` story groupings. For the initial integration,
   run tasks sequentially; parallelism is a future optimization. Don't
   burn design budget on it now.

## Summary

This pairing is the cleanest of the orchestration options we've
surveyed. Spec Kit's weaknesses (checkbox-equals-done, no
enforcement) are exactly clanker's strengths (verification, scope
enforcement, forensic trace). The integration surface is small: a
bridge that expands task lines into contracts, ambient-context
injection for `constitution.md`, a checkbox-flip MCP tool gated on
verified success.

We don't need to commit to this now. But if we do commit, the
changes in clanker are additive and unobtrusive — nothing in the
integration plan requires rearchitecting the executor. Phases 1-3
of the v2 plan happen regardless; the Spec Kit-specific work is a
thin layer on top.

Revisit this doc before phase 2 of v2-plan. By then we'll have
observability (phase 1) and actual experience running real contracts
— enough signal to either commit to the pairing or steer elsewhere.
