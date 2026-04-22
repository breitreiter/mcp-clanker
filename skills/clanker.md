---
name: clanker
description: Use when considering whether to delegate a rote, narrow-scoped coding task to clanker's build() executor (running on cheap/slow Azure GPT-5.1-codex-mini), when writing a clanker contract, or when interpreting a proof-of-work result. Covers the delegate-vs-do-in-context decision, contract structure, reading proof-of-work, blocked-question retry categories, and cost framing.
---

# clanker

mcp-clanker is an MCP server that hands a structured *contract* to a cheap, slow executor model (default: Azure GPT-5.1-codex-mini) which runs it in a fresh git worktree and returns a structured *proof-of-work*. Use it to keep Opus budget for judgment work and spend cheap tokens on rote grinding.

## MCP surface

Exposed by the `mcp-clanker` server:

- `build(contractPath)` — **long-running** (minutes to tens of minutes). Creates a worktree, runs the executor, returns proof-of-work JSON.
- `validate_contract(contractPath)` — dry-run (stub today).
- `list_tasks()`, `get_contract(taskId)`, `get_log(taskId)`, `update_contract(...)` — stubs today.
- Resources: `template://contract`, `template://proof-of-work`.

Build-time sidecar artifacts:
- `<parent>/<repo>.worktrees/<T-NNN>/` — the worktree
- `<parent>/<repo>.worktrees/<T-NNN>.trace/trace.jsonl` — forensic JSONL trace
- `<parent>/<repo>.worktrees/<T-NNN>.trace/transcript.md` — human-readable transcript

## When to delegate

Good candidates:
- You can list every file that should change. Scope is tight.
- Acceptance is 3–6 concrete, verifiable bullets. No "make it better."
- The work is mechanical once described (port a pattern, add a field, wire up config, add a test matching an existing shape).
- You don't need to watch the work unfold to trust it.

Don't delegate:
- Exploration, debugging, or anything requiring you to read code and form opinions turn by turn.
- Cross-cutting refactors that touch dozens of files.
- Design decisions, API-shape choices, or anything judgment-heavy.
- Vague goals ("clean up X", "improve Y").
- Tasks where one wrong early step cascades (architecture changes).

Heuristic: if you can't write crisp Acceptance bullets in under a minute, the contract isn't ready.

## Writing a contract

Fetch `template://contract` as the skeleton. Sections:

- **Goal:** one sentence — what changes in the world when this is done.
- **Scope:** exhaustive, explicit. `- edit: path` / `- create: path` / `- delete: path`. Scope is the single biggest lever on tool-call count and drift. Be ruthless.
- **Contract:** signatures, behaviors, constraints the implementation must honor. "What" not "how."
- **Context:** existing files the executor should read, with one-line "why it matters." Cheap orientation beats the executor re-deriving the codebase shape.
- **Acceptance:** concrete, verifiable checks. "All existing tests pass." "New tests cover case A, B, C." "No changes to files outside Scope."
- **Non-goals:** cheap to write, high leverage — they prevent rabbit-holing. List what this contract explicitly does NOT do.

Save at `<target-repo>/contracts/T-NNN-slug.md` (convention; not yet enforced). Pick `T-NNN` as the next unused task number.

Common contract mistakes:
- Scope too broad (`- edit: src/`) — the executor will wander. List specific files.
- Missing Non-goals — the executor decides for itself what's adjacent enough to touch.
- Acceptance written as aspirations ("code is clean") rather than checks ("no lines over 120 chars in edited files").

## Running a build

1. Write the contract file.
2. Call `build(contractPath)`. It blocks; go do something else.
3. Read the returned proof-of-work JSON.

The worktree is on a branch `contract/T-NNN`. It is not automatically cleaned up — merge, cherry-pick, or `git worktree remove` yourself.

## Reading proof-of-work

Shape (see `template://proof-of-work` for full schema). Check in this order:

1. **`terminal_state`**: `success` | `failure` | `rejected` | `blocked`.
   - `success` — executor completed without asking a question. Does not mean the work is correct; v1 has no closeout verification. Eyeball the diff.
   - `failure` — executor gave up after tool-call budget or similar.
   - `rejected` — contract is structurally wrong (missing section, scope file doesn't exist). See `rejection_reason`.
   - `blocked` — executor hit something it couldn't resolve. See `blocked_question`.

2. **`scope_adherence.in_scope`**: false means files changed outside declared Scope. `out_of_scope_paths` lists them. Investigate — either the contract's Scope was incomplete, or the executor colored outside the lines.

3. **`files_changed`**: what actually moved. Cross-check against Scope.

4. **`notes`**: free-text summary from the executor. Carries real signal — not ceremony. Read it.

5. **`blocked_question` / `rejection_reason`**: see retry loop below.

6. **`estimated_cost_usd`**, **`tokens_input_total`**, **`tokens_output_total`**: rough signal on spend. Cost is a hand-rated placeholder — order-of-magnitude only.

7. **`transcript_path`**: human-readable markdown of the run. Open this before `trace_path` (JSONL) — the transcript shows turn-by-turn model text, tool calls, and results in a scannable form.

8. **`worktree_path`**, **`branch`**: where the work lives. Navigate there to inspect the diff.

**`acceptance[]` is empty in v1.** Closeout verification hasn't shipped yet. `terminal_state=success` is not the same as "acceptance passed" — you must verify by reading the diff.

## The retry loop

When `terminal_state=blocked`, `blocked_question.category` tells you what to do:

- **`clarify_then_retry`** — contract was missing information the executor couldn't infer. Add the missing context (file paths, expected behavior, constraint) and re-run with the same task id.
- **`revise_contract`** — contract was contradictory or wrong. Rewrite the contract. May need to narrow Scope, clarify Acceptance, or fix Non-goals.
- **`rescope_or_capability`** — Scope was too wide, or the task needed a capability the executor doesn't have (a tool, a permission). Split into smaller contracts or defer.
- **`abandon`** — executor is asking for human judgment, not information. Don't retry; decide yourself.
- **`transient_retry`** — provider/infra hiccup. Re-run unchanged.

When `terminal_state=rejected`: read `rejection_reason`. Fix the contract structurally — usually a missing section or a scope file that doesn't exist — and re-run.

When `terminal_state=failure`: open `transcript_path`, read what happened, decide whether to re-run (maybe with tighter Scope) or abandon.

Before re-running: `git worktree remove <worktree_path>` and `git branch -D contract/T-NNN`, or the next build on the same task id will fail "worktree path already exists."

## Cost framing

Why delegate at all:
- Executing in-context on Opus: roughly $1–5 per non-trivial task.
- Delegating via `build()`: typically $0.03–0.30.
- The other half of the value: while the cheap executor grinds, Opus is free to do something else.

When in doubt: if a task is on the boundary between "delegate" and "do in-context," bias toward delegation when the task is clearly-scoped and toward in-context when it's exploratory or judgment-heavy.

## Known v1 limitations

Keep these in mind when interpreting results:

- **No closeout verification.** `acceptance[]` is empty. Success state is self-reported.
- **No safety gates.** The executor has `bash` with no command classifier, no network-egress check, no doom-loop detector. Contracts should not ask for destructive or network-bound work until v2 ships.
- **No sandbox.** Executor runs on the host in a git worktree. Home directory is reachable.
- **Limited toolset.** `bash`, `read_file`, `write_file`, `edit_file` only. No `grep`, `list_dir`, `todo_*`, `apply_patch` yet.
- **Manual cleanup.** Worktrees and branches are not auto-removed.
- **`retry_count` is always 0.** In-loop retries not yet implemented.
- **STUB handlers.** `list_tasks`, `get_contract`, `get_log`, `validate_contract`, `update_contract` all return stub strings. Work with contract files directly.

## Quick reference

| Step | Action |
|---|---|
| Decide to delegate | Scope listable? Acceptance writable in 3–6 bullets? Mechanical work? |
| Draft contract | Fetch `template://contract`; fill all six sections |
| Save | `<target-repo>/contracts/T-NNN-slug.md` |
| Run | `build(contractPath)` |
| Read result | `terminal_state` → `scope_adherence` → `files_changed` → `notes` |
| Inspect work | Open `transcript_path`; navigate to `worktree_path` |
| Retry | Match `blocked_question.category` to the action above |
| Clean up | `git worktree remove` + `git branch -D contract/T-NNN` when done |
