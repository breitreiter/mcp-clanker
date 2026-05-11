---
kind: plan
title: LSP integration for the executor
state: exploring
created: 2026-04-22
updated: 2026-05-01
provenance:
  source: project-migrate-skill:M-2026-05-10-1855
  migrated_at: 2026-05-10
touches:
  files:
    - Tools/
    - Build/Executor.cs
    - Build/Worktree.cs
    - Infrastructure/TraceWriter.cs
    - Prompts/closeout.md
  features:
    - executor-tool-surface
    - sandbox
---

# LSP integration for the executor

Outcome: not yet started. Cheap-first-step (Roslyn-only diagnostics,
host-side, C# only) is the recommended near-term experiment to
validate the hypothesis before committing to the sandbox refactor.

Backlogged. Not starting soon. Captured here so it can be picked up
cold.

## Premise

The GPT-54 instance imp spins up to execute a contract is a real
coding agent. Its world is the tool surface in `Tools.cs` —
`bash`, `read_file`, `write_file`, `apply_patch`, `grep`, `list_dir`,
`todo_read/write`, `finish_work`. Everything is text-shaped. Calls
to `grep` find string occurrences, not semantic references; reads
return bytes, not types; `apply_patch` rewrites characters, not
symbols.

That's the well-documented failure mode of text-only coding agents: miss
call sites on rename, hallucinate types, edit one of three overloads.
Closeout (`Prompts/closeout.md`, fresh-context reviewer over the diff)
catches some of it, but only after the executor has already burned tool
calls producing the wrong diff.

LSP is the standard answer. An IDE gives a human go-to-def, find-refs,
hover types, diagnostics, symbol search, AST-aware rename. Giving the
agent the same primitives is a direct lever on coding quality, targeted
at a real failure mode rather than speculation.

## Where LSP earns its keep, ranked

1. **`find_references` / `rename`.** Highest. The cases where
   `apply_patch`-by-string-match silently produces a broken codebase
   that compiles in the touched files. Overloads, interface
   implementors, shadowed names, call sites in unread files. Closeout
   catches some of these (it can re-grep), but only after the cost.
2. **`goto_definition` / `hover`.** Medium. Each "read 4 files to figure
   out what this returns" cycle becomes one structured call. Compounds
   across a run; visible in tool-call-count histograms.
3. **`diagnostics` without a build.** Medium. Today the agent learns
   about compile errors only by running `dotnet build` / `tsc --noEmit`
   through bash — expensive turns. Pulling diagnostics directly from
   the language server catches errors mid-edit, before the build/closeout
   loop. Big-O win on iteration count for typo-class errors.
4. **`workspace/symbol` search.** Low–medium. Marginally better than
   regex grep when the symbol name collides with common English words
   or substrings. Skip until #1–3 are validated.

Code completion is not on this list — the agent generates whole spans,
it does not type a character at a time.

## Integration shape

What "add LSP" actually means in this repo:

- **LSP client in-process.** `OmniSharp.Extensions.LanguageClient`
  exists; rolling JSON-RPC on `StreamJsonRpc` is also tractable. Either
  way it's a real C# dependency — see "new deps are Opus territory."
- **Per-worktree server lifecycle.** Spawn on executor start, kill on
  terminal state. One language server per worktree matches the existing
  isolation model in `Worktree.cs`. Lifecycle hooks slot into
  `Executor.RunAsync`'s setup/teardown.
- **Language detection + config.** Map repo shape → which server to
  spawn. C# (Roslyn / OmniSharp), TS/JS (tsserver), Python (pyright),
  Rust (rust-analyzer), Go (gopls). Start with one — C# is the
  dogfooding language.
- **Model-exposed tools.** Six new entries on the executor's tool surface,
  matching the ranked list:
  - `lsp_definition(file, line, col)`
  - `lsp_references(file, line, col)` (or by symbol name)
  - `lsp_hover(file, line, col)` — returns type + doc
  - `lsp_rename(file, line, col, new_name)` — returns a multi-file edit
    the agent then applies via `apply_patch`, or auto-applies and
    records into `ExecutorState.FilesTouched`
  - `lsp_diagnostics(file?)` — current diagnostics, scoped or
    repo-wide
  - `lsp_symbols(query)` — workspace symbol search
- **Prompt updates.** The system prompt (and skill) need to teach the
  agent when to reach for LSP vs grep — otherwise it'll keep grepping
  out of habit. "If you're about to rename a symbol, use `lsp_rename`,
  not `apply_patch` over `grep` results" is the kind of guidance.
- **Trace integration.** `TraceWriter` should record LSP calls with
  the same fidelity as bash/apply_patch. The `finish_work` citation
  field already supports "lsp_references result showing X" as a
  citation form.

## The hard part: Docker sandbox

`Tools.BuildBashProcess` runs each bash command in a fresh
`docker run --rm` container with the worktree bind-mounted at `/work`.
Per-command containers are cheap because of the persistent nuget volume.

Language servers don't fit that model. They're long-lived and stateful
— OmniSharp / tsserver want to live alongside the editing process with
shared FS state and incremental indexing. Three options, in order of
escalating engineering:

1. **Sidecar LSP container per worktree.** A second long-lived
   container, same `--network=none` and same worktree volume mount,
   running the language server. Bash commands continue to use ephemeral
   containers; LSP gets its own. Lifecycle managed by `Executor`.
   Probably the right answer for Docker mode but doubles the container
   choreography.
2. **Run LSP on the host against the bind-mounted worktree.** Cheaper
   to build; loses the sandbox property for whatever the language
   server does (LSPs do execute project code in some configurations —
   tsserver loads tsconfig, OmniSharp loads MSBuild, both can run user
   build scripts). For a hostile codebase this is not safe; for the
   solo-dev use case it may be acceptable. Document the gap.
3. **Skip LSP when sandbox mode is Docker.** Host-mode-only feature.
   Avoids the design problem entirely at the cost of making LSP a
   dev-only convenience. Probably wrong long-term but defensible for
   a first pass.

None of these is free. The cheap-first-step below sidesteps the whole
issue.

## Cold-start cost

OmniSharp, tsserver, rust-analyzer all take real seconds-to-minutes to
index a repo. Contract runs are short enough that per-run amortization
is not a given. LSP only wins if indexing happens *once* per worktree
and then the server stays warm across the run. With the worktree-per-
contract model that's automatic; the question is whether contracts are
long enough on average for indexing to pay back.

The dogfooding measurement: median tool-call count and wall-clock per
contract today. If contracts average 5 minutes of executor wall-clock
and OmniSharp takes 30s to index, ~10% overhead is plausibly worth it
for the rename/refs gain.

## Cheap first step

Before committing to the sandbox refactor, validate the hypothesis with
the smallest possible bet:

**Roslyn-only diagnostics, in-process, host-side, C# only.**

- `Microsoft.CodeAnalysis.Workspaces.MSBuild` runs in imp's process
  alongside the executor, no external server, no Docker complication.
- Expose **one** new tool: `lsp_diagnostics([file?])` — returns current
  diagnostics for the file or the whole workspace.
- Measure: tool-call counts before/after on a small contract batch.
  Does the agent call `bash dotnet build` less? Does closeout bounce
  less for typo-class errors?

A day or two of work. Tells us whether the richer integration is worth
the sandbox refactor and per-language scaffolding. If diagnostics
alone don't move the needle, rename/refs probably won't either, and
the OpenClaw argument is wrong for *this* agent (perhaps gpt-5.1-codex-
mini already handles text-based tools well enough for the contracts we
write). If diagnostics move the needle visibly, that's the green light
for the bigger build.

## What this is not

- **Not a Claude Code plugin-bundle thing.** OpenClaw's other argument
  — that LSP belongs in the host because Claude Code plugin bundles
  declare `.lsp.json` next to `.mcp.json` — is about plugin authoring.
  Imp is a standalone CLI tool, not a Claude Code plugin bundle.
  If a future user wants to bring their own LSP config, reading a
  `.lsp.json` from the target worktree is a tiny convenience that rides
  on a known convention; it's not a driver for this work.
- **Not a `nb` port.** `nb` doesn't have LSP either. There is no prior
  art to crib from; this is a from-scratch design pass.
- **Not a v2 blocker.** v2's improvements (closeout, retry semantics,
  spawn) all compose with text-based tools. LSP is parallel, not
  upstream.

## Decision points to revisit when picking up

- Single language (C#) first, or framework that admits multiple from
  day one? Recommend single — extract the abstraction once tsserver
  also lives in the box.
- Auto-apply rename results vs return-as-patch-for-the-agent-to-apply?
  Auto-apply is fewer turns; return-as-patch keeps `apply_patch` the
  single write path and trace stays uniform. Probably return-as-patch.
- Diagnostics push (subscribe + buffer) vs pull (tool call returns
  current state). Pull is simpler and matches the rest of the tool
  surface; push could let `Executor` short-circuit a turn that
  introduces a fatal error. Start pull.
- `lsp_rename` semantics for symbols defined outside the worktree
  (NuGet packages, stdlib). Refuse, or apply only to in-tree
  references? Refuse — imp doesn't edit dependencies.

## Measurement plan

If we build past the cheap first step, instrument:

- Tool-call histogram, broken out by tool name. LSP usage should
  *replace* grep/read calls, not stack on top of them.
- `dotnet build` / equivalent invocations per contract — should drop.
- Closeout bounce rate for compile-error-class failures — should drop.
- Wall-clock per contract, including LSP cold-start. Should not
  regress; if it does, indexing is too expensive for our contract size.
- Rename-class contracts specifically: hand-author a small set of
  contracts that require renaming a symbol with N>5 references in M>2
  files. Measure success rate with and without LSP. This is the
  contract type the whole exercise is for.
