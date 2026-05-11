---
kind: reference
title: Original imp design brief (seed doc, nb-build era)
created: 2026-04-21
updated: 2026-05-10
provenance:
  source: project-migrate-skill:M-2026-05-10-1855
  migrated_at: 2026-05-10
  migrated_from: project/BRIEF.md
topics: [architecture, history, contract-format, proof-of-work, state-machine, sub-agents]
touches: []
---

# Original imp design brief (seed doc, nb-build era)

> **Archival note (migrated 2026-05-10).** This is the original seed
> design doc, preserved for historical context. It was written when the
> project was going to be called `nb-build`, layer on top of `nb`, and
> expose itself to Claude Code as `nb-mcp` over stdio MCP. The
> high-level framing — the cost model, the contract artifact, the
> proof-of-work artifact, the build state machine, the sub-agent design
> — all still apply and are still cited from `CLAUDE.md`. The
> implementation details have moved on: imp grew its own executor
> instead of wrapping nb's, the MCP layer was replaced with a bash CLI
> invoked via a skill, and the cheap executor is now Azure
> GPT-5.1-codex-mini (not "GPT-54"). For current architecture see
> `CLAUDE.md`; for the rewrite history see
> `imp/learnings/mcp-to-cli-rewrite.md`. Sections describing nb-mcp,
> the MCP tool surface, and "reuse from nb" are kept as historical
> context.

A structured work-execution system. Turns an Opus/Claude Code planning
session into a queue of contracts that a cheap, slow model (originally
"GPT-54 on Azure"; now Azure GPT-5.1-codex-mini) can grind through
reliably, with automated closeout verification and structured
proof-of-work returned on completion.

This doc exists to seed the repo. It describes the architecture, the
data formats, what to build new, what to reuse from `nb`, and what's
explicitly out of scope.

## Problem this solves

A solo dev working on a complex codebase with:

- Hard budget cap on Anthropic services (~$200/mo)
- Effectively unlimited but slow Azure OpenAI (GPT-54)
- Rate-limited concurrent Azure sessions
- A task backlog too large to hold in working memory
- Cheap-model drift on bookkeeping steps (todo updates, test writing, docs)
- Context-switch cost every time the slow model finishes a minor task

The existing `nb` tool already drives GPT-54 reliably for individual tasks.
What's missing is a structured way to express work such that:

1. The planning model (Opus, via Claude Code) generates executable units
2. The execution model (GPT-54) grinds each unit without human babysitting
3. Closeout (tests, docs, todo updates) is guaranteed, not hoped for
4. The human gets a compact, scannable proof-of-work per completed unit

## Architecture

Three layers. Each does what it's good at; the boundaries are sharp.

**Claude Code (orchestration, via skill)** — talks to the human, writes
contracts, kicks off builds, reads proof-of-work artifacts, diagnoses
failures. Uses Opus/Sonnet tokens, so used sparingly and only at
judgment-heavy moments.

**nb-mcp (stdio MCP server)** — exposes contract operations to Claude Code
as tools. Thin wrapper around nb's build functionality. This is how Claude
Code reaches into the task system.

**nb executor (existing, extended)** — runs the actual build state machine,
drives GPT-54 on Azure, spawns sub-agents, writes logs. This is where the
grinding happens.

The cost model: Claude Code is called per-handoff (one prompt in, one proof-
of-work out), not continuously. Execution is paid for in Azure tokens,
which are effectively free. Opus only burns budget when the human asks it
to plan or diagnose.

## Core concepts

### Contract

The atomic unit of work. A markdown file with a known structure. Sufficient
detail that GPT-54 can execute without clarification; narrow scope that
keeps execution under ~100 tool calls; explicit enough that sub-agents
(test writer, doc writer, verifier) can consume the same document for their
jobs.

Template:

```markdown
## T-NNN: short descriptive title

**Goal:** One sentence. What changes in the world when this is done.

**Scope:**
- create: path/to/new/file.ts
- edit: path/to/existing/file.ts
- edit: path/to/tests/file.test.ts

**Contract:**
- Exported function signatures, types, or APIs that will exist.
- Key behaviors for important inputs.
- Behavior for edge cases.
- Purity / side-effect constraints.

**Context:**
- path/to/related.ts — why it matters (one-liner).
- path/to/another.ts — why it matters (one-liner).

**Acceptance:**
- All existing tests pass.
- New tests cover: case A, case B, case C.
- Docs updated at path/to/doc.md if public API changed.
- No changes to files outside Scope.

**Non-goals:**
- This task does NOT do X (that's T-NNN).
- This task does NOT do Y.

**Depends on:** T-NNN, T-NNN  (or "none")
```

Scope is the biggest lever on tool-call count. Explicit file lists prevent
the agent from reading half the repo "to be safe."

Non-goals matter. Cheap to write, saves hours of rabbit-holing.

### Proof of work

Structured artifact returned when `build` completes. Known schema, every
time, so Claude Code develops fluency reading them without needing the
full log. The log stays on disk for when the human wants to dig in.

Schema (shown as JSON; serialize to markdown for display but keep the
machine-readable form available):

```json
{
  "task_id": "T-042",
  "terminal_state": "success | failure | rejected | blocked",
  "started_at": "ISO-8601",
  "completed_at": "ISO-8601",
  "tool_call_count": 47,
  "retry_count": 1,
  "files_changed": [
    { "path": "src/foo.ts", "action": "created", "lines_added": 38, "lines_removed": 0 }
  ],
  "tests": {
    "added": ["resolverHandlesEmptyStack", "resolverStacksSameType"],
    "modified": [],
    "existing_passed": true
  },
  "acceptance": [
    { "item": "All existing tests pass", "status": "pass" },
    { "item": "New tests cover empty/single/stacked cases", "status": "pass" },
    { "item": "Docs updated at CONDITIONS.md", "status": "pass" },
    { "item": "No changes outside Scope", "status": "pass" }
  ],
  "sub_agents_spawned": [
    { "role": "closeout", "verdict": "pass", "notes": "..." }
  ],
  "notes": "Free-text. Surprises, deviations, things the human might want to eyeball. This field carries real signal — don't treat it as ceremony.",
  "blocked_question": null,
  "rejection_reason": null
}
```

On `blocked`, `blocked_question` is populated and `rejection_reason` is null.
On `rejected`, the inverse. The contract file itself gets a `## Blocked on`
or `## Rejected` section appended so state is visible on disk.

### Build state machine

```
parse_contract
    ↓
validate (scope files exist, deps satisfied, contract well-formed)
    ↓
execute_loop (bounded; GPT-54 grinds within Scope)
    ↓
closeout_subagent (fresh context, checks acceptance items)
    ↓
pass? → finalize / fail? → back to execute_loop (bounded retries, max 2)
    ↓
terminal_state + proof_of_work
```

Terminal states:
- **success** — all acceptance items pass
- **failure** — acceptance items failed after retry budget exhausted
- **rejected** — contract itself is wrong (bad signature, contradictory acceptance, missing scope file)
- **blocked** — model has a question it cannot resolve from the contract

The state is written back into the contract file (or a sidecar `.state.md`)
so `/build` on the same file is resumable. Crash-safe; can be restarted
after rate limits or SIGKILL.

### Sub-agent spawn

Model-exposed tool, not harness-magic. GPT-54 decides when to spawn during
execution. Signature:

```
spawn(prompt: str, files: list[str], output_schema: dict) → structured_result
```

Fresh context per spawn. The parent writes a curated briefing; the child
does not inherit the parent's context. The repo map (see below) is
available to all sessions as cheap orientation.

Depth limit: 2 (parent → child → grandchild, stop). Prevents fork-bomb
from a confused agent.

Closeout is the exception — it's harness-driven, not model-exposed. Fires
on execute-loop completion, validates acceptance against actual filesystem
state, feeds gaps back to the execute loop if retries remain.

### Repo map

Cheap, cached summary of the codebase available to all sessions (main loop
and sub-agents). File tree + one-line purpose per module + key exported
signatures. Generated on first run, invalidated on file changes. Lets
sub-agents orient in a few hundred tokens instead of reading 20 files.

Not an LLM-generated artifact — static analysis is fine. The one-line
purposes can be LLM-generated on initial creation, then human-editable.

## What to reuse from nb

The existing `nb` codebase has already solved the hard mechanical problems.
Do not rebuild:

- Azure OpenAI SDK integration and auth
- Streaming response handling
- Tool call harness / agent loop
- Existing todo tool (keep it; see below)
- Risk assessment and approval flows
- Skill selection via keyword maps
- Shell access primitives
- ChromeOS / Kubuntu dev environment bits

The existing todo tool continues to serve its current purpose inside the
execute loop. The contract system is a layer *above* the todo tool — GPT-54
breaks a contract into todos as part of execution, and the todo tool
handles the "keep going until finished" nudging. No reason to replace it.

## What's new

The actual scope of work for this project:

- **Contract parser** — reads the markdown template into a structured form
- **Contract validator** — checks scope files exist, deps are satisfied, required sections present
- **Build command** — orchestrates the state machine
- **Proof-of-work serializer** — produces the structured artifact
- **Closeout sub-agent** — prompt + verification logic that checks acceptance
- **Spawn tool** — exposed to the model, fresh-context sub-agent invocation
- **Repo map** — generator and cache
- **nb-mcp** — stdio MCP server exposing build operations
- **Claude Code skill** — tells Claude Code how to read/write contracts and proof-of-work

## MCP tool surface

Exposed by `nb-mcp`:

- `build(contract_path)` — long-running; returns proof-of-work on completion
- `list_tasks()` — returns task IDs, titles, states from the contract directory
- `get_contract(task_id)` — returns contract content
- `get_log(task_id)` — returns full execution log (for diagnosis; Claude Code calls this only when proof-of-work isn't enough)
- `validate_contract(contract_path)` — dry-run; reports well-formedness and scope-file existence without executing
- `update_contract(contract_path, content)` — writes a new or revised contract

Claude Code's skill teaches it the contract format, the proof-of-work
schema, and the escalation pattern (blocked/rejected → read log → rewrite
contract → retry).

## Design invariants

A few things the implementation should not drift from:

- **The cheap model runs the outer loop.** Orchestration is bookkeeping,
  not judgment. Paying Opus rates for orchestration is wasteful. Claude
  Code participates at handoff boundaries, not during execution.
- **Proof-of-work is structured, not prose.** Same fields every time.
  Free-text only in the `notes` field.
- **Contracts are the source of truth.** All state writes back to the
  contract file or its sidecar. No in-memory session state that can't
  survive a restart.
- **Sub-agents get curated briefings, not inherited context.** If you
  find yourself wanting to pass the parent's full context, the parent's
  context is too big and the real fix is upstream.
- **The harness owns guaranteed steps; the model owns judgment calls.**
  Closeout verification is harness-driven because it must happen. Spawn
  decisions are model-driven because they require judgment about when
  delegation is worth it.

## Non-goals

Explicitly out of scope for the initial build:

- Parallel task execution. Azure rate limits make this pointless until
  multi-account support is solved, and serial execution isn't the
  bottleneck anyway.
- Formal verification (Lean/Coq/Dafny). Way too heavy. "Tests pass +
  acceptance items check out" is sufficient.
- A planning model inside `/build`. Planning is Opus/Claude Code's job,
  outside `/build`. If the contract is vague, the build returns
  `rejected` and the human (or Claude Code) fixes it and re-runs. Do
  not let `/build` try to plan.
- Multi-repo support. One repo at a time.
- Non-Anthropic, non-Azure model backends. Single-executor for now.
- A web UI. Everything is files and CLI.

## Prior art to draw from

The task/contract template design should lean on these, with attribution
in the repo README:

- **GitHub Spec Kit** (`github/spec-kit`) — the closest existing work on
  spec/plan/tasks decomposition. Their `templates/tasks-template.md` and
  `templates/plan-template.md` are well-road-tested starting points.
  We deliberately adopt only the artifact shapes, not the full
  Constitution → Specify → Plan → Tasks → Implement methodology (too
  heavy for this use case — see Scott Logic's critique for the failure
  mode of adopting the whole thing).
- **Coordinator/Implementor/Verifier pattern** — Augment Code has a good
  writeup. Claude Code is Coordinator; nb executor is Implementor;
  closeout sub-agent is Verifier. The pattern is peer-reviewed in
  VeriMAP (EACL 2026), though reading the paper is not necessary to
  build this.
- **Google Antigravity's "verifiable artifacts"** — source of the
  proof-of-work framing. We steal the *idea* of a structured, auditable
  completion record; we don't adopt their platform.

## Testing strategy

This is the core research work. The architecture only earns its keep if
it measurably works. Things to instrument from day one:

- **Terminal-state distribution** per contract batch. What fraction are
  success / failure / rejected / blocked? If rejected rate is high, the
  planning layer is weak. If failure rate is high, the execute loop or
  closeout is weak. If blocked rate is high, contracts are too vague.
- **Tool call count per contract.** Histogram. Outliers suggest
  contracts with too-broad Scope.
- **Retry count.** How often does closeout bounce tasks back to the
  execute loop, and does the retry succeed? If retry usually fails,
  retrying is wasted tokens and the budget should be cut to 1.
- **Closeout compliance rate.** What fraction of execute-loop terminations
  already satisfy acceptance before closeout nudges them? Rising
  compliance over time is evidence the contract format and prompts are
  getting better.
- **Time-to-completion.** Per-contract wall clock. Useful for deciding
  whether a contract is sized correctly.
- **Budget consumption.** Tokens per Anthropic call (Claude Code side) and
  per Azure call (nb side), aggregated per contract.

Build a golden set of contracts with known outcomes — small, medium,
deliberately-vague, deliberately-contradictory — and run the full
pipeline against them in CI. When prompts or the state machine change,
compare metrics against the golden set. This is the difference between
"I feel like it's working" and knowing.

Prompt iteration is where most quality comes from. Version the prompts
in-repo, log which version ran for each contract, make A/B comparison
possible.

## Open questions

Things to decide during implementation, not now:

- Exact contract file location convention — `contracts/T-NNN-slug.md`
  in the repo root? In a sibling repo? Config-driven?
- Whether proof-of-work goes back into the contract file or a sidecar
  `.state.md`. Sidecar is cleaner for git diffs; inline is one-file-per-
  task which is nice. Probably sidecar, but try both.
- How aggressive closeout should be about the `No changes to files
  outside Scope` check. Strict (any out-of-scope diff fails) or
  permissive (warn but allow)? Start strict and loosen if needed.
- Whether the spawn tool should have per-role prompt templates (one for
  test-writing, one for doc-writing, one for verification) or just be
  general-purpose. Probably start general and extract templates once
  patterns emerge.

## Suggested build order

A rough order that front-loads the parts that are hard to change later:

1. Contract format + parser + validator. Lock the format before
   building anything that consumes it.
2. Proof-of-work schema + serializer. Lock this shape early too.
3. Basic `build` command with the state machine, wired to existing `nb`
   execute loop, no sub-agents yet. Validate end-to-end with a single
   trivial contract.
4. Closeout sub-agent. Test against deliberately-failing contracts.
5. Repo map generator.
6. Model-exposed spawn tool.
7. nb-mcp stdio server.
8. Claude Code skill.
9. Metrics instrumentation and golden test set.
10. Iterate on prompts and thresholds based on metrics.

Stop after each step and validate before moving on. The whole point of
the design is to avoid the trap of building elaborate orchestration
before knowing if the base layer works.
