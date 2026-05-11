---
kind: plan
title: Redesign imp as a CLI tool
state: shipped
created: 2026-04-23
updated: 2026-05-01
shipped: 2026-05-01
touches:
  files: [Program.cs, skills/imp.md]
  features: [host-shape, cli, mcp-removal]
provenance:
  source: project-migrate-skill:M-2026-05-10-1855
  migrated_at: 2026-05-10
related:
  - imp/learnings/mcp-to-cli-rewrite.md
---

# Redesign imp as a CLI tool

Outcome (2026-05-10): Shipped. Phases 1-4 complete — imp runs as a CLI
invoked from Claude Code via Bash + `skills/imp.md`. MCP layer removed.
`imp build`, `imp validate`, `imp review`, `imp ping`, `imp template`,
`imp list`, `imp show`, `imp log` are all live subcommands. Phase 5
(self-contained binary distribution) deferred. See
`imp/learnings/mcp-to-cli-rewrite.md` for the why.

## Premise

The MCP stdio shape has cost more than it's bought us. Every
substantive friction point we've hit on the path to v2 has been
MCP-shaped:

- Tool changes require restarting the long-lived subprocess.
- DLL lock on Windows blocks `dotnet build` while the subprocess
  is running (CLAUDE.md gotcha section).
- Claude Code does not capture the MCP server's stderr to disk
  ([anthropics/claude-code#29035](https://github.com/anthropics/claude-code/issues/29035)).
- Subprocess state can drift across requests; the symptom on
  2026-05-01 was a 13-minute hang on Windows that left zero
  on-disk evidence anywhere.
- Running the same `Build()` code path via the existing
  `dotnet run -- --build <contract>` CLI in `Program.cs:101`
  succeeded immediately on the same machine.

Meanwhile the only consumer of the MCP surface is Claude Code.
The MCP layer's strongest remaining argument — interop with
non-Claude-Code clients — is hypothetical, and an MCP wrapper
could be re-added later as a thin shim over the CLI if it ever
matters.

What we lose: tool descriptions auto-injected into the parent
model's tool catalog, and resource templates. A Claude Code skill
markdown carries the prose, and a `imp template <name>`
subcommand replaces the resource templates. The skill (today at
`skills/imp.md`) is already 80% of the work.

What we gain: every invocation a fresh process (no state drift),
stderr visible to the parent (no opaque hangs), no DLL lock on
Windows, no restart-to-pick-up-tool-changes cycle, and a far
smaller package footprint (drop `ModelContextProtocol` and almost
certainly `Microsoft.Extensions.Hosting`).

This plan is independent of v3 (LSP tools). They can ship in
either order. v3 is additive to the executor; this is structural
to the host shape.

## Parent's relationship to the worktree

A principle this plan must not weaken, because the host-shape
change makes it easier to violate by accident:

The whole point of imp is that the parent (Opus) delegates
rote work and trusts the proof-of-work JSON to summarize it. If
the parent reads source files inside the worktree to verify — or
worse, edits them — three things break at once: the cost premise
(you paid the executor *and* paid Opus to re-do it), the trust
premise (closeout exists exactly so the parent doesn't need to
spot-check), and the concurrency benefit (Opus is supposed to do
other work while cheap grinds, not babysit the diff).

The worktree is a PR-shaped artifact. The parent's interaction
with it is:

1. Read `terminal_state` + `acceptance[]` + `notes` from the
   proof-of-work JSON.
2. Run `imp review <task-id>` (see phase 1 below) for the
   bundled view: verdicts + scope check + files changed + diff.
3. Open `transcript_path` only if steps 1–2 look off.
4. Decide: merge, cherry-pick, request a revision contract, or
   abandon. Clean up.

Reaching into individual source files inside the worktree is a
smell that one of: the contract was underspecified, closeout
missed something, or the work shouldn't have been delegated. The
right response is to fix the upstream cause, not to fix the diff
in-place.

Two concrete commitments in this plan enforce that boundary:

- **Phase 1 ships `imp review <task-id>`** so the parent has
  one obvious command to run; reaching into the worktree requires
  extra, conscious effort.
- **Phase 2 ships an explicit "what the parent does and doesn't
  do with a worktree" section in the skill,** replacing the
  current permissive "navigate to `worktree_path`" prose
  (`skills/imp.md:92`, `skills/imp.md:94`).

If observed behavior in phase 3 shows the parent still digging
into the worktree, the cause is one of: skill prose isn't strong
enough, closeout isn't trustworthy enough, or the contract was
wrong — none of which are fixable by undoing this plan.

## Surface mapping

| MCP today | CLI replacement | Output |
|---|---|---|
| `build(contractPath, targetRepo?)` | `imp build <contract-path>` | JSON to stdout (the proof-of-work) |
| `validate_contract(contractPath, targetRepo?)` | `imp validate <contract-path>` | JSON to stdout |
| `list_tasks(targetRepo?)` | `imp list` | JSON array to stdout |
| `get_contract(taskId, targetRepo?)` | `imp show <task-id>` | Markdown to stdout |
| `get_log(taskId, targetRepo?)` | `imp log <task-id>` | Markdown to stdout |
| *(new)* | `imp review <task-id>` | Markdown bundle to stdout: terminal_state, acceptance verdicts, scope_adherence, files_changed, notes, plus rendered `git diff main...contract/T-NNN`. The canonical "what to do after a build" command — keeps the parent out of the worktree. |
| `update_contract(path, content)` | *removed* — parent uses `Write`/`Edit` directly | n/a |
| `ping([provider])` | `imp ping [provider]` | Provider response to stdout |
| `template://contract` resource | `imp template contract` | Markdown to stdout |
| `template://proof-of-work` resource | `imp template proof-of-work` | JSON to stdout |

Conventions:

- `targetRepo` parameter goes away entirely — the CLI uses cwd, no
  override. The MCP-era flag was already marked for removal in
  `McpTools.cs:27` and `skills/imp.md:68`.
- Errors go to stderr, exit non-zero only for catastrophic
  failures (can't read contract file, unhandled exception). A
  `Rejected` result is still exit 0 with the rejection in the
  JSON — keeps parent parsing uniform.
- Existing `imp.log` (added 2026-05-01) keeps working
  unchanged. With per-invocation processes, log rotation matters
  even less than today.

## The path

| Phase | Goal | Ships | How you validate | Sessions |
|---|---|---|---|---|
| **1. Subcommand router + parity + `review`** | CLI exposes the full MCP surface, no MCP code touched yet, *plus* the new `imp review` affordance that keeps the parent out of the worktree | `Program.cs` becomes a real subcommand dispatcher (System.CommandLine or hand-rolled — decide at phase start); add the missing verbs (`list`, `show`, `log`, `validate`, `template`, plus rename existing `--build` etc. to `build`); each verb calls the same `McpTools.*` static method as today; **add `imp review <task-id>`** which loads proof-of-work for the most recent run, formats verdicts + scope check + files changed + notes, and shells out to `git diff main...contract/T-NNN` for the diff body | `imp build <contract>` and `imp validate <contract>` produce identical JSON to the MCP tool calls; `imp template contract` matches `template://contract` byte-for-byte; `imp review T-NNN` on a finished build produces a single-screen bundle that contains everything the parent needs to merge/abandon decisions without opening files in the worktree | 1–2 |
| **2. Skill rewrite + worktree-boundary section** | Parent model knows to use the CLI, not MCP, *and* knows the worktree is a PR-shaped artifact, not a workspace | `skills/imp.md` updated: replace every `build(contractPath)` etc. with `imp build <path>`; drop the "MCP surface" section, add a "CLI reference" section; update the quick-reference table; document the one-time `imp *` permission allowlist for Claude Code; **add an explicit "Parent's relationship to the worktree" section** with the four-step interaction list above; rewrite the current permissive "navigate to `worktree_path`" prose (`skills/imp.md:92`, `skills/imp.md:94`) to "trust closeout verdicts; spot-check the diff via `imp review` if something looks off" | Read the new skill cold and try to delegate a small task — instructions should be self-contained without referencing MCP; the skill should make `imp review` the obvious next step after `build`, with reading source files in the worktree framed as a smell | 1 |
| **3. End-to-end on Windows + boundary validation** | The original bug is fixed, *and* the parent demonstrably stays out of the worktree | A real contract run on the Windows machine via Claude Code → bash → `imp build`; observe stderr in Claude Code's terminal output; confirm hang scenarios surface as visible errors; observe the parent's post-build behavior to confirm it runs `imp review` rather than reading worktree files | One real contract executes end-to-end on Windows; one deliberately-broken contract (missing scope file) returns a visible Rejected; one git-timeout scenario surfaces the 30s timeout from `Worktree.cs`; the parent's tool-call trace shows `imp review` rather than `Read` calls into the worktree | 1 |
| **4. MCP removal** | The dead layer is gone | Strip `[McpServerTool]` attributes from `McpTools.cs`; remove the `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()` chain from `Program.cs`; remove `ModelContextProtocol` and (likely) `Microsoft.Extensions.Hosting` package refs; remove the MCP-subprocess-rebuild gotcha section from `CLAUDE.md`; rename the project from Imp → Imp if we feel like it | `dotnet build` still passes; package count drops; `Program.cs` shrinks; CLAUDE.md no longer warns about subprocess locks | 1 |
| **5. Distribution polish (optional)** | `imp` is a binary on $PATH, no `dotnet run` | `dotnet publish -c Release -r <rid> --self-contained` produces a single-file binary; document install via a `./install.sh` (Linux/macOS) or copy-to-PATH (Windows); update the skill to use `imp` instead of `dotnet run --project ...` | One install step on a fresh machine, then `imp build` works without the .NET SDK | 1–2 |

**~4–6 sessions for phases 1–4. Phase 5 is independent and can wait.**

Phase 1 must ship before any of the others. Phases 2–3 must ship
together (the skill must be live before Windows validation, and
Windows validation must succeed before MCP removal). Phase 4
requires phases 1–3 done. Phase 5 is opt-in polish.

## Open design questions

Not blockers, but call them out early so they don't churn mid-phase:

- **Subcommand naming.** Flat (`imp build`, `imp validate`)
  or nested (`imp contract build`, `imp contract list`)?
  Flat is simpler and matches `git`'s top-level verb style. Lean
  flat unless we hit a collision.
- **`--json` flag for text-output commands.** `imp show` is
  markdown today; if the parent ever wants structured output,
  add `--json` then. Don't pre-design.
- **Where the skill lives.** `skills/imp.md` in the repo is
  the source of truth, but Claude Code discovers skills from
  `.claude/skills/` (project) or `~/.claude/skills/` (user). The
  install story should copy or symlink. Decide in phase 2.
- **Permission UX.** Today every MCP tool call is a separate
  Claude Code prompt. Bash gets prefix-allowlisted, so
  `imp *` would unlock all subcommands at once. Document this
  as a setup step; it's strictly a UX win after the first
  approval.
- **`appsettings.json` discovery.** Already loads from
  `AppContext.BaseDirectory` (`Program.cs:44`). Works fine for a
  CLI; no change needed.
- **Renaming the project.** `Imp.csproj` → `Imp.csproj`
  is cosmetic. Defer to phase 4 or skip; it'll churn imports but
  buys nothing.
- **`imp remove <task-id>` cleanup affordance.** Same theme
  as `imp review`: make the right path easier than the wrong
  one. Today the skill tells the parent to run
  `git worktree remove <path>` + `git branch -D contract/T-NNN`
  by hand, which is friction at exactly the moment the parent
  has decided to abandon. A single subcommand that does both,
  refusing to remove if the worktree has uncommitted changes,
  would round out the lifecycle. Add to phase 1 if it's cheap;
  defer to a later phase if it isn't. Not load-bearing for the
  rest of the plan.

## What's deliberately not in scope

- Re-adding an MCP wrapper later. If a non-Claude-Code client
  needs MCP, write a thin shim that shells out to the CLI. Not
  this plan's problem.
- Changing the executor, the contract format, the safety gates,
  the closeout reviewer, the Docker sandbox, the trace shape, or
  any v2/v3 in-flight work. This plan is purely about the host.
- Cross-platform installer beyond `dotnet publish`. A homebrew
  formula or winget package is later.

## Honesty check

This plan trades one well-understood failure mode (MCP subprocess
opacity on Windows) for a slightly different one (parent model
has to learn the CLI surface from a skill, not from the tool
catalog). If the skill is well-written, the trade is clearly
positive. If the parent model fumbles CLI invocations because the
skill is incomplete, the cost is "Claude Code asks for permission
on a slightly-wrong command" — recoverable in seconds, not in
hours.

The biggest hidden cost is one-time: rewriting the skill so it
reads cleanly to a model that has never seen the MCP version.
Phase 2 budgets a session for that; if it bleeds, the bleed is
contained.

The fixes shipped on 2026-05-01 (`ImpLog`, `RunGit` 30s
timeout) are not predicated on this plan. They help the MCP path
and the CLI path equally; commit them either way.

The worktree-boundary commitments (`imp review`, the explicit
skill section) are the part of this plan most likely to be tested
in practice. If phase-3 validation shows the parent still
reaching into the worktree, treat that as a signal about closeout
trustworthiness or skill prose, not as a reason to add more
guardrails on the CLI side.
