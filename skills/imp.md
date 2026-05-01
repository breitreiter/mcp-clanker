---
name: imp
description: Use when considering whether to delegate a rote, narrow-scoped coding task to imp's `imp build` executor (running on cheap/slow Azure GPT-5.1-codex-mini), when writing a imp contract, or when interpreting a proof-of-work result. Covers the delegate-vs-do-in-context decision, contract structure, reading proof-of-work, blocked-question retry categories, and cost framing.
---

# imp

imp is a CLI tool that hands a structured *contract* to a cheap,
slow executor model (default: Azure GPT-5.1-codex-mini) which runs
it in a fresh git worktree and returns a structured *proof-of-work*.
Use it to keep Opus budget for judgment work and spend cheap tokens
on rote grinding.

## Setup

imp runs as a bash command. One-time, allow it in Claude Code's
permission settings:

```
imp *
```

That permits all subcommands; per-subcommand allowlisting works too
(`imp build *`, `imp review *`, etc.) but rarely worth the
friction.

Per-invocation the parent runs `imp <verb> [args]` from the
target repo's root. Each invocation is a fresh process — no
long-lived MCP subprocess, no state drift between calls.

## CLI reference

Lifecycle:

- **`imp build <contract-path>`** — runs the executor against a
  contract. Long-running (minutes to tens of minutes). Emits
  proof-of-work JSON to stdout. Persists `proof-of-work.json`,
  `transcript.md`, and `trace.jsonl` to the trace dir.
- **`imp validate <contract-path>`** — dry-run: parses the
  contract and checks structural validity + scope-file existence,
  no model call. Emits validation JSON to stdout. Use before
  `build` to catch contract errors without paying for a turn.
- **`imp review <task-id>`** — the canonical post-build view.
  Emits a markdown bundle: proof-of-work JSON + `git diff
  HEAD...contract/T-NNN`. **This is what you run after `build` —
  not "navigate into the worktree."** See "Parent's relationship
  to the worktree" below.

Inspection:

- **`imp list`** — JSON array of contracts under
  `./contracts/*.md` with task IDs and titles.
- **`imp show <task-id>`** — prints the contract markdown.
- **`imp log <task-id>`** — prints the rendered transcript of
  the most recent run. Use when proof-of-work notes aren't enough
  to diagnose what happened.
- **`imp template <name>`** — prints a template (`contract` or
  `proof-of-work`). Pipe to a file to start a new contract:
  `imp template contract > contracts/T-NNN-slug.md`.

Diagnostics:

- **`imp ping [provider]`** — smoke-test provider round-trip.
- **`imp ping-tools [provider]`** — verify multi-turn tool-call
  round-tripping.

Build-time sidecar artefacts (per task):

- `<parent>/<repo>.worktrees/<T-NNN>/` — the worktree
- `<parent>/<repo>.worktrees/<T-NNN>.trace/proof-of-work.json` — structured result
- `<parent>/<repo>.worktrees/<T-NNN>.trace/transcript.md` — human-readable run log
- `<parent>/<repo>.worktrees/<T-NNN>.trace/trace.jsonl` — forensic JSONL
- `<exe-dir>/imp.log` — append-only host-side log of every CLI invocation

## Parent's relationship to the worktree

**The worktree is a PR-shaped artefact, not a workspace.** When you
delegate work to imp, your job is to evaluate the result, not
to redo it. Reading source files inside the worktree, or — worse
— editing them, breaks three things at once:

- **Cost premise.** You paid the executor *and* paid Opus to redo
  the work.
- **Trust premise.** Closeout exists exactly so you don't need to
  spot-check.
- **Concurrency benefit.** Opus is supposed to do other work while
  cheap grinds, not babysit the diff.

Your interaction with a finished build is, in order:

1. Read the proof-of-work JSON returned by `imp build` — start
   with `terminal_state`, then `acceptance[]` verdicts, then
   `notes`.
2. Run **`imp review <task-id>`** for the bundled view: the
   proof-of-work plus `git diff HEAD...contract/T-NNN`. One screen
   of markdown. This is the right command nine times out of ten.
3. Run **`imp log <task-id>`** *only if* steps 1–2 surfaced
   something suspicious. The transcript carries every turn the
   executor took.
4. Decide: merge, cherry-pick, request a revision contract, or
   abandon. Then clean up: `git worktree remove <worktree-path>`
   and `git branch -D contract/T-NNN`.

If you find yourself opening individual source files inside
`<repo>.worktrees/T-NNN/` with `Read`, that's a smell. Three
likely causes, in priority order:

- The contract was underspecified (Scope or Acceptance was vague,
  so closeout had nothing concrete to verify against).
- Closeout missed something (rare; if it happens repeatedly, log
  it as a closeout-trust issue).
- The work shouldn't have been delegated (the task was actually
  judgment-heavy or exploratory and the contract obscured that).

Fix the upstream cause — don't fix the diff in-place. The right
response to a bad delegation is a better contract or a decision
not to delegate, not Opus quietly redoing the work in the same
worktree.

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
- **Anything that adds a new package dependency** (`dotnet add package`, `npm install <new>`, `cargo add`, etc.). When imp runs in its Docker sandbox, the container has no network — only packages already in the cache work. Packages the project hasn't adopted yet will fail to restore. That's deliberate: package-adoption is a judgment call you own, not something to delegate. Adopt the dep yourself, then delegate the work that uses it.

Heuristic: if you can't write crisp Acceptance bullets in under a minute, the contract isn't ready.

## Writing a contract

Run `imp template contract` for the skeleton. Sections:

- **Goal:** one sentence — what changes in the world when this is done.
- **Scope:** exhaustive, explicit. `- edit: path` / `- create: path` / `- delete: path`. Scope is the single biggest lever on tool-call count and drift. Be ruthless.
- **Contract:** signatures, behaviors, constraints the implementation must honor. "What" not "how."
- **Context:** existing files the executor should read, with one-line "why it matters." Cheap orientation beats the executor re-deriving the codebase shape.
- **Acceptance:** concrete, verifiable checks. "All existing tests pass." "New tests cover case A, B, C." "No changes to files outside Scope."
- **Non-goals:** cheap to write, high leverage — they prevent rabbit-holing. List what this contract explicitly does NOT do.

Save at `<target-repo>/contracts/T-NNN-slug.md` (convention; not enforced). Pick `T-NNN` as the next unused task number — `imp list` shows what's taken.

Common contract mistakes:
- Scope too broad (`- edit: src/`) — the executor will wander. List specific files.
- Missing Non-goals — the executor decides for itself what's adjacent enough to touch.
- Acceptance written as aspirations ("code is clean") rather than checks ("no lines over 120 chars in edited files").

## Running a build

**Pre-flight: `git status` must be clean in the main checkout.** imp creates the worktree from `HEAD`. Uncommitted or unstaged changes in the main checkout aren't in the worktree (so the executor can't see them) and reconciling them across two divergent trees afterwards is fiddly — stashing back and forth is the usual way it goes wrong. Commit your in-flight work, or stash with explicit intent to pop it later, *before* running `imp build`.

1. Write the contract file.
2. `imp validate <contract-path>` to confirm it parses and the scope files exist.
3. `imp build <contract-path>`. It blocks for minutes to tens of minutes; go do something else.
4. Read the proof-of-work JSON that comes back on stdout.
5. `imp review <task-id>` for the bundled diff + summary.

The worktree is on a branch `contract/T-NNN`. It is not automatically cleaned up — merge, cherry-pick, or `git worktree remove` yourself.

**Target repo:** imp operates on the current working directory.
The intended flow is one Claude Code session per target repo, so cwd is implicit. There's no override flag — `cd` if you need to.

## Reading proof-of-work

Shape (see `imp template proof-of-work` for full schema). Check in this order:

1. **`terminal_state`**: `success` | `failure` | `rejected` | `blocked`.
   - `success` — executor completed AND an independent closeout reviewer verified every Acceptance bullet. `acceptance[]` carries closeout's verdicts with citations; `sub_agents_spawned[]` has a `{role: closeout, verdict: pass|mixed|fail}` entry.
   - `failure` — executor gave up after tool-call budget or similar. A `success`-then-demoted run (closeout caught something) also lands here.
   - `rejected` — contract is structurally wrong (missing section, scope file doesn't exist). See `rejection_reason`.
   - `blocked` — executor hit something it couldn't resolve. See `blocked_question`.

2. **`scope_adherence.in_scope`**: false means files changed outside declared Scope. `out_of_scope_paths` lists them. Investigate by re-reading Scope and the contract — either Scope was incomplete, or the executor colored outside the lines.

3. **`files_changed`**: what actually moved. Cross-check against Scope.

4. **`notes`**: free-text summary from the executor. Carries real signal — not ceremony. Read it.

5. **`blocked_question` / `rejection_reason`**: see retry loop below.

6. **`estimated_cost_usd`**, **`tokens_input_total`**, **`tokens_output_total`**: rough signal on spend. Cost is a hand-rated placeholder — order-of-magnitude only.

7. **`transcript_path`**, **`worktree_path`**, **`branch`**: pointers, not destinations to dig into. `imp review <task-id>` already bundles what you need; `imp log <task-id>` opens the transcript when you need turn-by-turn detail.

Trust closeout verdicts. Spot-check the diff via `imp review` if `terminal_state=success` but something in `notes` or `scope_adherence` looks off — but don't reflexively re-read source files in the worktree just because you can.

## The retry loop

When `terminal_state=blocked`, `blocked_question.category` tells you what to do:

- **`clarify_then_retry`** — contract was missing information the executor couldn't infer. Add the missing context (file paths, expected behavior, constraint) and re-run with the same task id.
- **`revise_contract`** — contract was contradictory or wrong. Rewrite the contract. May need to narrow Scope, clarify Acceptance, or fix Non-goals.
- **`rescope_or_capability`** — Scope was too wide, or the task needed a capability the executor doesn't have (a tool, a permission). Split into smaller contracts or defer.
- **`abandon`** — executor is asking for human judgment, not information. Don't retry; decide yourself.
- **`transient_retry`** — provider/infra hiccup. Re-run unchanged.

When `terminal_state=rejected`: read `rejection_reason`. Fix the contract structurally — usually a missing section or a scope file that doesn't exist — and re-run.

When `terminal_state=failure`: `imp log <task-id>` for the transcript, decide whether to re-run (maybe with tighter Scope) or abandon.

Before re-running: `git worktree remove <worktree_path>` and `git branch -D contract/T-NNN`, or the next build on the same task id will fail "worktree path already exists."

## Cost framing

Why delegate at all:
- Executing in-context on Opus: roughly $1–5 per non-trivial task.
- Delegating via `imp build`: typically $0.03–0.30.
- The other half of the value: while the cheap executor grinds, Opus is free to do something else.

When in doubt: if a task is on the boundary between "delegate" and "do in-context," bias toward delegation when the task is clearly-scoped and toward in-context when it's exploratory or judgment-heavy.

## Known limitations

Keep these in mind when interpreting results:

- **Closeout reviewer is in.** `acceptance[]` is populated by an independent read-only reviewer after success runs. A `success` terminal means the reviewer passed every bullet; `failure` after closeout means the reviewer caught something — second-guess by reading the trace and the closeout citations.
- **Safety gates are in.** Danger-pattern (`rm -rf`, `sudo`, etc.), network-egress (`curl`/`wget`/`gh api` without localhost exemption), and doom-loop (3× same tool-args or 5 consecutive failures) detectors will block with the right `blocked_question.category`. Contracts that genuinely need network can declare an `Allowed network:` section.
- **Docker sandbox shipped, opt-in.** Set `Sandbox.Mode="Docker"` in `appsettings.json` and run `./sandbox/build.sh` to activate. In that mode each `bash` call runs in a throwaway container with `--network=none`, so a confused / compromised executor can't touch the host filesystem outside the worktree or reach the network. Default is still `Host` mode for backward compatibility.
- **Toolset:** `bash`, `read_file`, `write_file`, `apply_patch`, `grep`, `list_dir`, `todo_read`, `todo_write`. Prefer `apply_patch` for edits when running GPT-family.
- **Manual cleanup.** Worktrees and branches are not auto-removed.
- **`retry_count` is always 0.** In-loop retries not yet implemented.

## Quick reference

| Step | Action |
|---|---|
| Decide to delegate | Scope listable? Acceptance writable in 3–6 bullets? Mechanical work? |
| Draft contract | `imp template contract > contracts/T-NNN-slug.md`; fill all six sections |
| Pre-flight | `git status` clean in the main checkout — commit (or intentionally stash) before running |
| Validate | `imp validate contracts/T-NNN-slug.md` |
| Run | `imp build contracts/T-NNN-slug.md` |
| Read result | `terminal_state` → `scope_adherence` → `files_changed` → `notes` |
| Inspect | `imp review T-NNN` (bundled diff + proof) |
| Deeper inspect | `imp log T-NNN` (full transcript) |
| Retry | Match `blocked_question.category` to the action above |
| Clean up | `git worktree remove` + `git branch -D contract/T-NNN` when done |

The cardinal rule: **`imp review`, not Read into the worktree.**
