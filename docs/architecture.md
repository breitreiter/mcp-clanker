# Architecture

> **⚠ Stale (2026-05-01).** This doc describes the pre-CLI MCP-stdio
> architecture. The MCP layer was removed; imp is now a bash CLI
> invoked from Claude Code via the `imp` skill (see
> `skills/imp.md` for the user-facing surface, `project/cli-plan.md`
> for the rewrite plan). The build-flow / executor / sandbox / safety-gate
> sections below are still substantively correct — only the *transport*
> changed (MCP stdio → CLI subprocess). Treat the "MCP tool call
> arrives" framing as historical until this doc is rewritten.

How imp turns a contract markdown file into a verified piece of code change. Mechanical, top-down.

## Mental model

```
┌────────────────────────────────────────────────────────────────────────┐
│ Claude Code (parent, Opus)                                             │
│   - writes contract T-NNN-slug.md                                      │
│   - calls imp:build(contractPath)                              │
│   - reads returned proof-of-work JSON, decides merge / retry / abandon │
└─────────────────────────────┬──────────────────────────────────────────┘
                              │ stdio MCP
┌─────────────────────────────▼──────────────────────────────────────────┐
│ imp (this repo, C# stdio server)                               │
│   McpTools.Build:                                                      │
│     1. parse contract         (Contract.cs)                            │
│     2. validate contract      (ContractValidator)                      │
│     3. create git worktree    (Worktree.Create)                        │
│     4. run executor           (Executor.RunAsync)                      │
│     5. serialize POW          (BuildResult + BuildResultJson)          │
│                                                                        │
│   Executor.RunAsync runs three phases:                                 │
│     a. main loop — tool-call loop with full toolset                    │
│     b. self-check — one turn, finish_work only, model self-reports     │
│     c. closeout — fresh context, read-only + finish_work, verifies     │
└─────────────────────────────┬──────────────────────────────────────────┘
                              │ (per-command)
┌─────────────────────────────▼──────────────────────────────────────────┐
│ chat provider (Azure Foundry / OpenAI / Anthropic / Gemini)            │
│   - evaluates history, returns messages + tool calls + usage           │
└────────────────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────────────┐
│ bash subprocess  (Host mode)   OR   docker run --rm  (Docker mode)     │
│   - executes bash commands the model emits                             │
│   - workdir = the contract's worktree                                  │
└────────────────────────────────────────────────────────────────────────┘
```

Three key invariants:

1. **The executor runs in a disposable worktree.** A fresh git branch `contract/T-NNN` off the target repo's HEAD. Never touches the main checkout.
2. **The parent decides what to do with a result.** `terminal_state` is a report, not a gate. Imp returns POW and stops.
3. **Every verification is weak on its own; the set is strong.** Scope adherence + safety gates + closeout diff review each catch a different failure mode. See `safety.md` (TODO) or `docs/architecture.md#safety-architecture` below.

## Request lifecycle

Happy path, top-to-bottom, with file:line references where they matter.

### 1. MCP tool call arrives

Claude Code invokes `mcp__imp__build(contractPath, targetRepo?)` over stdio. The ModelContextProtocol server routes it to `McpTools.Build` (`McpTools.cs:20`).

### 2. Contract parse + validate

- `File.ReadAllTextAsync(contractPath)` → raw markdown string.
- `ContractParser.Parse(markdown)` (`Contract.cs:49`) — lenient parser. Missing sections become empty collections; never throws.
- `ContractValidator.Validate(contract, resolvedTargetRepo)` (`Contract.cs:139`) — fail-loud checks:
  - Goal section must be non-empty.
  - Scope section must have at least one entry.
  - Acceptance section must have at least one entry.
  - Every `edit:` / `delete:` scope entry must point at an existing file.
  - Every `create:` scope entry must have an existing parent directory.

On validation failure, `McpTools.Build` returns early with `terminal_state=rejected` and a `rejection_reason` — no worktree is created.

### 3. Worktree creation

`Worktree.Create(targetRepo, contract.TaskId)` (`Worktree.cs:25`):

- Path: `<parent-of-target>/<target-basename>.worktrees/<taskId>/`
- Branch: `contract/<taskId>`
- Implementation: shells out to `git worktree add <path> -b <branch>`.
- If the path already exists, throws rather than reusing. A stale worktree usually means the previous run's state wants inspection, not clobbering.

### 4. Executor.RunAsync

(`Executor.cs:19`.) This is the heart of the system. Three phases:

#### 4a. Main loop

Builds the initial chat history:

```csharp
[
  System: Prompts.LoadSystemPrompt(providerName, contract),
  User:   "Begin."
]
```

The system prompt is a markdown file (`Prompts/default.md`, or `Prompts/<provider>.md` if present) with `{{CONTRACT}}` replaced by the contract's raw markdown.

Options:
- `MaxOutputTokens = 16384` — reasoning models need room for hidden reasoning plus visible output plus tool calls.
- `Tools = Tools.Create(workingDirectory, state, sandbox)` — full toolset (see [Tool plane](#tool-plane)).

Each turn:

1. Check tool-call budget (default 500). If exceeded, terminate `blocked` / `abandon`.
2. `chat.GetResponseAsync(history, options, ct)`. Exceptions terminate `blocked` / `transient_retry`.
3. Write trace turn event (duration, usage, finish_reason, text).
4. Extract `FunctionCallContent` from the response.
5. **No tool calls in the response?** Check `finish_reason`:
   - `Length` → terminate `blocked` / `rescope_or_capability` with a "raise `MaxOutputTokens`" hint.
   - `ContentFilter` → terminate `blocked` / `abandon`.
   - Anything else (usually `Stop`) → terminate `Success`, set `notes = response.Text`.
6. **Tool calls present?** Invoke each via `InvokeTool`, append `FunctionResultContent`s, push into history, increment `state.ToolCallCount`, record in `state.RecentCalls`.
7. After the tool batch, run `DoomLoopDetector.Check(state.RecentCalls)`. On trip, flag `SafetyBreach` → terminate `blocked` / `abandon`.
8. Any pre-flight gate (CommandClassifier, NetworkEgressChecker) may have flagged a `SafetyBreach` during tool dispatch; check and terminate if so.

The main loop exits with `terminal ∈ {Success, Failure, Blocked}` (never `Rejected` — that's only from validation).

#### 4b. Self-check

(`Executor.cs:113` roughly, via the `RunSelfCheckAsync` method.) Runs only if `terminal == Success`.

- One extra model turn.
- Tools narrowed to `[finish_work]` only.
- `ToolMode = RequireAny` — the provider is expected to force a tool call.
- User prompt assembled by `BuildSelfCheckPrompt(contract.Acceptance)`: lists the acceptance bullets, demands `{item, status, citation}` per bullet.
- `finish_work`'s closure writes to `state.AcceptanceReports`.

If the model ignores the tool mandate, `AcceptanceReports` stays null and the run continues — the main work already landed cleanly, a missing self-check doesn't demote terminal.

#### 4c. Closeout (v2-plan Phase 5)

(`Executor.cs`, `RunCloseoutAsync`.) Runs only if `terminal == Success`.

Fresh chat history — not the executor's history. System prompt from `Prompts/closeout.md` (the reviewer role description).

User prompt assembled by `BuildCloseoutPrompt(contract, state.AcceptanceReports, diff)`:

- Contract goal.
- Acceptance bullets (numbered).
- Executor's self-report (for reference only — marked as untrusted).
- Diff of uncommitted worktree changes, captured via `CaptureWorktreeDiff`.

Diff capture:

```csharp
git add -N .           // mark untracked files as intent-to-add so git diff sees them
git diff HEAD          // full diff of uncommitted changes + new files
```

Truncated at 32 KB. The `-N` side effect on the worktree's index is harmless — the worktree is ephemeral.

Tools: `Tools.CreateReadOnly(workingDirectory)` (read_file, grep, list_dir) + a closeout-scoped `finish_work` that writes to `state.CloseoutReports`.

Agent loop, not single-turn:

- Model reads / greps as needed.
- When ready, calls `finish_work` with `reports: List<{item, status, citation}>` and optional `notes`.
- Budget: 20 tool calls. Exhaustion logs and leaves `CloseoutReports` null (self-report falls through).
- If the model ends a turn with text-only (no tool call), nudge message once; otherwise loop.

**Terminal-state demotion** (`Executor.cs`, inside the try block just after `RunCloseoutAsync`): if any report is `Fail`, `terminal` flips from `Success` to `Failure`, and `notes` gets an appended `"Closeout verdict: N of M acceptance items failed independent verification."` line.

The demotion happens before `trace.WriteEnd` so the trace records the final terminal, and the transcript agrees with the POW.

### 5. Build the POW

After all phases, `Executor.RunAsync` constructs the `BuildResult`:

- `files_changed` = `state.FilesTouched` (populated by WriteFile / ApplyPatchInvoke).
- `scope_adherence` = `CheckScopeAdherence(contract, files_changed)` — algorithmic, no LLM. Flags any changed file whose path isn't in the declared Scope.
- `estimated_cost_usd` = `Pricing.Estimate(modelName, tokensIn, tokensOut)`.
- `acceptance` = `state.CloseoutReports ?? state.AcceptanceReports` (closeout wins when present).
- `sub_agents_spawned` = single entry for closeout when it ran: `{role: closeout, verdict: pass | mixed | fail, notes}`.

The BuildResult is serialized by `BuildResultJson.Serialize` using snake_case keys with indented formatting.

### 6. Return

`McpTools.Build` returns the serialized JSON. Claude Code reads it. Imp forgets the run — no persistent state beyond the files on disk (worktree, trace, transcript).

## Execution state

`ExecutorState` (`Tools.cs:16`) is the only mutable object shared across tool invocations within a run. Lives for the duration of `Executor.RunAsync` and is discarded afterward.

| Field | Writer | Reader | Purpose |
|---|---|---|---|
| `ToolCallCount` | Executor main loop + closeout loop | Executor main loop (budget check) | Counts every dispatched tool call |
| `FilesTouched` | `RunBash` (no — just bash), `WriteFile`, `ApplyPatchInvoke` | BuildResult construction | Dictionary of path → FileAction for `files_changed` |
| `Todos` | `todo_write` tool | `todo_read` tool | Session-scoped todo list (TodoManager) |
| `RecentCalls` | Executor main loop (`RecordToolCall` after each dispatch) | `DoomLoopDetector.Check` | Last 10 tool calls (name + args signature + success) |
| `SafetyBreach` | `RunBash` (danger/egress pre-flight), `DoomLoopDetector` (post-batch) | Executor main loop (post-batch termination check) | First-write-wins; set-once then terminal |
| `AcceptanceReports` | `finish_work` during self-check | BuildResult construction (fallback) | Phase 4 model self-report |
| `CloseoutReports` | `finish_work` during closeout | BuildResult construction (authoritative if present), demotion check | Phase 5 independent verification |
| `CloseoutNotes` | `finish_work` during closeout (optional) | `sub_agents_spawned[].notes` | Closeout's free-text summary |

Notice: `ExecutorState` holds no history, no chat messages, no prompts. That state lives in the `List<ChatMessage> history` local variable of `Executor.RunAsync`. Closeout constructs its own fresh list so nothing leaks between the executor's and the reviewer's contexts.

## Tool plane

Imp exposes three different tool surfaces depending on the phase.

### Main-loop tools (`Tools.Create`)

Full toolset exposed to the executor during the main loop:

| Tool | File | Mutates? | Notes |
|---|---|---|---|
| `bash` | `Tools.cs:RunBash` | Yes (can do anything) | Pre-flights through CommandClassifier + NetworkEgressChecker; executes via `/bin/bash -c` (Host mode) or `docker run --rm` (Docker mode) |
| `read_file` | `Tools.cs:ReadFile` | No | Path clamped to workingDirectory; supports offset/limit |
| `write_file` | `Tools.cs:WriteFile` | Yes | Records to `state.FilesTouched`; creates parents |
| `apply_patch` | `Tools.cs:ApplyPatchInvoke` → `ApplyPatch.cs` | Yes | Codex sentinel format; BuildPreview + Apply split; records to `state.FilesTouched` |
| `grep` | `Tools.cs` → `GrepTool.cs` | No | Skips SkipDirectories, binary files; two output modes |
| `list_dir` | `Tools.cs:ListDir` | No | Same SkipDirectories set |
| `todo_read` | `Tools.cs` → `TodoManager.Render` | No (state only) | Returns current checklist |
| `todo_write` | `Tools.cs:WriteTodos` → `TodoManager.ApplyChanges` | Yes (state only) | Unique-content key; cancelled removes |

### Self-check tools

Exactly `[finish_work]`. `ToolMode = RequireAny`. Inline-registered in `RunSelfCheckAsync`; captures reports into `state.AcceptanceReports`.

### Closeout tools (`Tools.CreateReadOnly` + inline `finish_work`)

| Tool | Notes |
|---|---|
| `read_file` | Same implementation; no state writes |
| `grep` | Same |
| `list_dir` | Same |
| `finish_work` | Inline-registered in `RunCloseoutAsync`; captures to `state.CloseoutReports` (not `AcceptanceReports`) |

No `bash`, no `write_file`, no `apply_patch`, no `todo_*`. Closeout cannot modify worktree or run commands — it verifies what's already there.

## Safety architecture

Four layers of defense, each a different shape. Order by when they fire during a run:

### Layer 1: CommandClassifier (pre-flight, per bash call)

`CommandClassifier.cs`. Regex matcher against a fixed danger-pattern list (`rm -r`, `sudo`, `chmod 777`, `curl | sh`, writes into `/dev`, `/etc`, `/usr`, `/bin`, `/sbin`, `dd`, `mkfs`, `fdisk`) plus a multi-line heuristic (any line containing `rm` or a write redirect in a multi-line script is suspect).

On hit: `state.SafetyBreach` flagged with category `Abandon`. RunBash returns `ERROR:` without executing.

### Layer 2: NetworkEgressChecker (pre-flight, per bash call)

`NetworkEgressChecker.cs`. Blocks `curl`, `wget`, `nc`, `ncat`, `ssh`, `scp`, `sftp`, `rsync`, `ftp`, `telnet`, plus `gh api` and `gh <subcmd>` with mutating verbs (create/edit/delete/close/merge/upload/comment/review/…).

Exemption: any command containing a localhost marker (`localhost`, `127.0.0.1`, `::1`, `0.0.0.0`) passes through. Coarse by design — threat model is confused/misaligned model, not crafted-input bypass.

On hit: `state.SafetyBreach` with category `RescopeOrCapability` (remediation is to add an `**Allowed network:**` declaration — not yet implemented).

### Layer 3: DoomLoopDetector (stateful, post-tool-batch)

`DoomLoopDetector.cs`. Watches the tail of `state.RecentCalls`:

- N=3 consecutive calls with identical `(Name, ArgsSignature)` → model is stuck repeating.
- M=5 consecutive failures → model is banging its head.

On either trip: `SafetyBreach` with category `Abandon`. Runs only when no pre-flight gate already flagged.

### Layer 4: Closeout reviewer (fresh-context, post-main-loop)

Not a safety gate in the blocking sense — runs only after a clean `Success` terminal. Independently verifies each Acceptance bullet against the actual worktree diff. Fresh-context fork of the same model, read-only tools, evidence-forced citations.

On any `Fail` verdict: terminal demoted from `Success` to `Failure`. See `Phases/Closeout` above.

### Sandbox (parallel to all four, transport-level)

`SandboxConfig.cs`. Mode = `Host` means `bash` runs directly as `/bin/bash -c`. Mode = `Docker` means every bash call is a `docker run --rm` with `--network=<none>`, bind-mounted worktree, resource limits (memory, cpus, pids).

Sandbox is orthogonal to layers 1–4: those are application-layer checks, sandbox is process-isolation. A bash command that slipped past both the classifier and the egress gate would still be unable to reach the network or the host filesystem outside the worktree when running in Docker mode.

### Category matrix — which gate fires which `blocked_question.category`

| Trigger | Category | Remediation |
|---|---|---|
| CommandClassifier hit | `abandon` | Don't retry; rewrite the approach |
| NetworkEgressChecker hit | `rescope_or_capability` | Add `**Allowed network:**` (future) or remove the network call |
| DoomLoopDetector hit | `abandon` | Don't retry; the contract might be misspecified |
| Tool-call budget exhausted | `abandon` | Narrow the scope; raise budget if genuinely large |
| Chat provider exception | `transient_retry` | Re-run unchanged |
| `finish_reason=length` with no tool calls | `rescope_or_capability` | Raise `MaxOutputTokens` or narrow scope |
| `finish_reason=content_filter` | `abandon` | Manual judgment |
| ContractValidator reject | *(rejected, not blocked)* | Fix the contract structure |
| Closeout verdict = fail | *(demoted to `failure`, not blocked)* | Parent reads citations and decides |

## Worktree + trace layout on disk

For a target repo at `<parent>/<name>` running contract T-NNN:

```
<parent>/<name>/                           # the main checkout — untouched during the run
<parent>/<name>.worktrees/
├── T-NNN/                                 # the worktree (fresh git branch contract/T-NNN)
│   ├── …existing repo contents…
│   └── …files the executor wrote/edited…  # visible in worktree's `git status`
└── T-NNN.trace/
    ├── trace.jsonl                        # append-only JSONL; one line per event
    └── transcript.md                      # rendered human-readable version
```

None of this is auto-cleaned. The parent (Claude Code) decides whether to merge, cherry-pick, or remove.

## Process boundaries

- **Claude Code ↔ imp**: stdio MCP. JSON-RPC framed over stdin/stdout. Started by Claude Code as a long-lived subprocess (`dotnet run --project <repo>`). Claude Code → server: tool calls. Server → Claude Code: tool results + MCP resource reads.
- **imp ↔ chat provider**: HTTPS via Microsoft.Extensions.AI abstractions. One `IChatClient` instance built from `appsettings.json`'s active provider.
- **imp ↔ bash**: `Process.Start` per bash call. In Host mode, the process is `/bin/bash`; in Docker mode, it's `docker` (which itself spawns a container).
- **imp ↔ git**: `Process.Start` invocations for worktree create and diff capture.

imp has no persistent state outside the target repo's worktrees directory. A restart loses all in-flight runs (there are none — `build` blocks until it returns).

## Extension points

When adding to the system, the common shapes:

- **New tool exposed to the executor** — register an `AIFunctionFactory.Create(...)` entry in `Tools.Create` (`Tools.cs`). If it mutates files, call `state.RecordWrite`. If its args are load-bearing for debugging, add the tool name to `TraceWriter.IsMutationOrCommand`.
- **New safety gate (pre-flight on bash)** — write a pure classifier (see `CommandClassifier.cs` or `NetworkEgressChecker.cs` for shape); call it inside `RunBash` before process startup; flag `state.SafetyBreach` with the appropriate `BlockedCategory`.
- **New safety gate (stateful, post-batch)** — add fields to `ExecutorState` to accumulate signal; write a static `Check(state)` method (see `DoomLoopDetector.cs`); invoke it in `Executor.RunAsync` after the tool batch.
- **New chat provider** — extend `Providers.Create` to recognize the new `Name` and return an `IChatClient`.
- **New phase (after closeout)** — add a method on `Executor`, call it inside the main try-block (so `trace.WriteEnd` captures its effects), update `BuildResult` construction to include whatever it produced.

Things NOT to extend lightly:

- The `BuildResult` / POW JSON schema — Claude Code's skill is written against the current shape. Add fields, don't rename.
- The trace JSONL event types — `TranscriptRenderer` parses them; older traces should still render.
- The `ExecutorState` mutability pattern — current state is: tool closures capture `state` by reference, writes happen synchronously. Adding async writes or thread-safety invites race conditions the current code isn't prepared for.
