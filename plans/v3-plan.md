---
kind: plan
title: Plan to reach v3
state: exploring
created: 2026-04-25
updated: 2026-05-10
provenance:
  source: project-migrate-skill:M-2026-05-10-1855
  migrated_at: 2026-05-10
---

# Plan to reach v3

Outcome: not started. Phase 1 is gated on a v2-trace review
(see "Honesty check" below) that hasn't happened yet.

## Premise

v3 is quality uplift, not a capability gate. v2's bar is "you trust
this system enough to use it at work." v3's bar is "the executor
makes fewer structural mistakes because it can ask the language
server instead of inferring from grep."

The expected lift is modest. Contracts are narrow-scope by design,
so the model gets decent orientation from `grep` / `read_file` /
`list_dir` today. LSP tools win in two places:

1. **Diagnostics** — compile / type errors surfaced in-loop, before
   closeout. Cheaper than a test run and catches a different class
   of mistake (shape, not behavior).
2. **Structural lookups** — `goto_definition` and `find_references`
   return real facts instead of regex guesses that miss on
   extension-method calls, partial classes, generics, etc.

**v3 must not block usage.** Every tool is additive. Executor
without LSP behaves exactly as today. Config-gated, per-language
opt-in, fail-open on server unavailable. No path in the loop
requires LSP to proceed.

Prior art: opencode and similar CLI coding agents ship LSP
toolsets and report coding-quality lift. Worth emulating at the
tool-surface level; not worth adopting their full architecture.

## The path

| Phase | Goal | Ships | How you validate | Sessions |
|---|---|---|---|---|
| **1. LSP client + C# tools** | Model can ask structured questions of a language server | Minimal JSON-RPC LSP client; per-build C# language server spawn (Roslyn LSP or OmniSharp — decide at phase start); five tools — `goto_definition`, `find_references`, `hover`, `document_symbols`, `diagnostics`; `Lsp` section in appsettings; fail-open if server can't start | Run a medium C# contract with LSP enabled vs disabled; trace shows the model substituting LSP calls for grep/read_file on structural lookups; disabled run is unchanged | 2–3 |
| **2. Sandbox parity** | LSP works under `Sandbox.Mode=Docker` | Language-server binary baked into sandbox image; per-build lifecycle inside the container; stdio bridged through the same exec path as bash | Phase-1 contract re-run with `Sandbox.Mode=Docker` — tools respond, diagnostics surface, `--network=none` still holds | 1 |
| **3. Polyglot (gated on phase 1–2 signal)** | Second language via the same harness | `typescript-language-server` (or equivalent) wired via the phase-1 abstraction; per-language LSP config in appsettings | TS contract uses LSP tools end-to-end; ship only if phase 1–2 showed material quality lift, else defer | 1–2 |

**~4–6 sessions if we run all three.** Phase 3 is optional by
design — see honesty check below.

## Parallel track: CLI boundary hygiene

Independent of LSP. Three small items surfaced by reading
pal-mcp-server, a mature Python MCP server with ~9 months of
iteration — the boundary lessons port cleanly to imp's CLI shape.
None affect executor quality; all three improve the signal surface
the parent agent and operator see.

| Item | Ships | Why it earns its keep | Validate | Sessions |
|---|---|---|---|---|
| **A. Structured error payloads** | Failure responses from `build` / `validate` / `update_contract` wrap as JSON `{status, category, reason, suggestion}` on stdout, with a non-zero exit code; categories enumerated (e.g. `blocked_question`, `sandbox_failure`, `tool_budget_exhausted`, `closeout_rejected`, `invalid_contract`) | Parent agent can route on category without regexing prose; operator triage is faster | Parent handles each category distinctly in a short scripted run; categories cover every terminal non-success seen in v2 traces | 1 |
| **B. Activity log** | `logs/activity.log`, append-only, one line per event: contract start/end, tool call, sandbox up/down, closeout verdict; rotates on size (reuse whatever's cheap); stays alongside the JSONL trace, does not replace it | Tailable operational view without opening a trace file; makes "what is imp doing right now" a one-command answer | `tail -f` during a live build shows meaningful events in order; trace remains the forensic source of truth | <1 |
| **C. Continuation offers** | `BuildResult` gains `suggestedFollowUps: string[]` (short enum: `codereview`, `precommit`, `open_pr`, `revise_contract`, `retry`) and optional `nextAction: string` (one-line human hint) | Parent agent picks the next move from a structured list instead of parsing the prose summary; keeps the executor honest about what it thinks should happen next | Across ~5 contracts, parent picks an appropriate follow-up from the list without reading the rendered transcript | 1 |

**~2–3 sessions total.** Items are independent; ship in any order.
No dependency on LSP work, no dependency between items.

**Avoid, explicitly:**

- **Item A** should not grow a taxonomy richer than the categories
  that actually appear in v2 traces. Add categories when a real
  run produces one, not speculatively.
- **Item B** is not a second trace. One line per event, no
  payloads, no structured fields beyond a timestamp and verb. If
  you want detail, open the JSONL.
- **Item C** must not let the executor choose follow-ups from
  brittle heuristics ("saw the word test, suggest precommit").
  Suggestions come from terminal state + a small rule table, or
  they don't ship.

## Why LSP at all (and why not earlier)

Structural tools are exactly the weak signal that composes: each
call is cheap, the model picks which to use, and the combined
effect is fewer wrong-path turns. They earn their keep on runs
where the contract touches an unfamiliar API or the model needs
to reason about types across files.

We deferred past v2 on purpose. Without v2's instruments (trace,
transcript, closeout verdict) there's no way to tell whether LSP
helps — "it feels better" is not data. v2's proof-of-work plus
reviewer verdict give a before/after signal per contract.

**Diagnostics is the load-bearing tool of the five.** If only one
ships, ship diagnostics: catching compile errors before closeout
is the cheapest possible quality gate, and it composes with the
Phase 4 self-check and Phase 5 reviewer already in place.

## Honesty check

v3 earns its seat only if v2 produced runs where the trace
visibly shows the model thrashing on something an LSP tool would
have answered cleanly — missed references, wrong-type inference
from `read_file`, or a compile error that surfaced only at
closeout.

Before starting phase 1, go back through v2 traces and list the
three strongest cases. If the list is empty, v3 is premature and
the session budget belongs elsewhere.

## Explicitly deferred past v3

- **`rename_symbol` / workspace-edit tools.** `apply_patch`
  handles this at acceptable quality today. LSP rename is
  cleaner but not load-bearing.
- **`code_actions` / quick-fix round-tripping.** Requires
  applying server-returned edits back into the worktree. More
  complex, unclear lift until phase-1 data comes in.
- **Push-based diagnostics after every write.** Phase 1 is
  pull-based (model calls the tool). Automatic surfacing needs
  a prompt pattern we don't have; decide after we see how models
  use the pull version.
- **Multiple language servers per session.** YAGNI until a
  polyglot contract shows up. Phase 3 swaps servers; it doesn't
  run them concurrently.
- **Cross-repo / workspace-wide symbol index.** Scope-narrow
  contracts are the design; no need to index beyond the worktree.

## See also

- `plans/v2-plan.md` — v3 depends on v2's instruments for its
  honesty check; the data we need to justify v3 is produced by
  v2 runs
- `TODO.md` — v1 and v2-deferred items; v3 work items land here
  only after the pre-phase-1 review
- `project/BRIEF.md` — original framing; LSP tools are a v3
  addition, not anticipated in the initial scope
- `project/lsp-integration-research.md` (migrating in parallel) —
  research notes feeding the phase-1 server choice
