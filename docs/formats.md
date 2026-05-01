# File formats

Reference for the four on-disk/on-wire formats imp produces or consumes. Intended for someone writing a consumer: a dashboard, a parser, a sibling agent, a migration tool.

For how these files move through the system, see `architecture.md`.

## Contract (markdown, input)

The atomic unit of work. Written by the parent, consumed by `build()`.

Filename convention: `<target-repo>/contracts/T-NNN-slug.md`. The path is convention, not enforced — `build()` accepts any absolute path.

### Parser

`ContractParser.Parse(string markdown)` in `Contract.cs`. Lenient: missing sections degrade to empty collections, never throws. Fail-loud rejections happen at validation time (`ContractValidator.Validate`), not parse time.

### Sections

All sections are optional from the parser's perspective. The validator enforces which must be populated for a build to proceed.

```markdown
## T-NNN: short descriptive title
```

- Matched by `^##\s*(T-\d+)\s*:\s*(.+?)\s*$` (multiline, first match wins).
- `task_id` = the `T-NNN` capture. Used for branch naming (`contract/T-NNN`) and worktree directory (`<repo>.worktrees/T-NNN`).
- `title` = the text after the colon.
- If the line is missing, `task_id="T-???"` and `title="(untitled)"`.

```markdown
**Goal:** One sentence. What changes in the world when this is done.
```

- Matched by `\*\*Goal:\*\*\s*(.+?)(?=\r?\n\r?\n|\r?\n\*\*|\z)` (singleline).
- Whitespace-collapsed to one line.
- **Validator: non-empty required.**

```markdown
**Scope:**
- create: path/to/new/file.ts
- edit: path/to/existing/file.ts
- delete: path/to/obsolete/file.ts
```

- Parsed by `^\s*-\s*(create|edit|delete)\s*:\s*(.+?)\s*$` (multiline, case-insensitive).
- `ScopeAction ∈ {Create, Edit, Delete}`.
- Unrecognized action strings default to `Edit`.
- **Validator:** at least one entry required. Each entry:
  - `edit` / `delete` — file must exist in target repo.
  - `create` — parent directory must exist in target repo.
- Used at runtime by `CheckScopeAdherence` for the `scope_adherence` POW field.

```markdown
**Contract:**
- Exported function signatures, types, or APIs that will exist.
- Key behaviors for important inputs.
- Behavior for edge cases.
- Purity / side-effect constraints.
```

- Free-form text (not bulleted). The entire section body is captured verbatim as `ContractBody`.
- Injected into the system prompt via the `{{CONTRACT}}` token.

```markdown
**Context:**
- path/to/related.ts — why it matters (one-liner).
```

- Parsed by `^\s*-\s*(.+?)\s*[—-]\s*(.+?)\s*$`.
- Each entry = `(Path, Note)`.
- Used only as input to the model (appears in the system prompt via `{{CONTRACT}}`). Imp itself doesn't read these files eagerly.

```markdown
**Acceptance:**
- All existing tests pass.
- New tests cover: case A, case B.
- Docs updated at path/to/doc.md if public API changed.
- No changes to files outside Scope.
```

- Parsed by generic bullet regex `^\s*-\s*(.+?)\s*$`.
- **Validator:** at least one entry required.
- Used at runtime by self-check (bullets handed to the model for verdict) and closeout (same, independently).

```markdown
**Non-goals:**
- This task does NOT do X.
- This task does NOT do Y.
```

- Same bullet regex.
- Input to the model only. No runtime enforcement.

```markdown
**Depends on:** T-NNN, T-NNN
```

or

```markdown
**Depends on:** none
```

- Comma-separated task IDs, or the literal word `none` / empty.
- Input only; imp does not orchestrate dependencies.

### Section detection

Sections are found by scanning for `**SectionName:**` and capturing everything until the next `**OtherSection:**` header or EOF. Order doesn't matter; duplicates get the first match.

## Proof-of-work (JSON, output)

Returned from `build()` as a JSON string. Shape defined by `BuildResult` in `BuildResult.cs`, serialized by `BuildResultJson.Serialize` with snake_case keys and indented formatting.

Example of a successful run that passed closeout:

```json
{
  "task_id": "T-005",
  "terminal_state": "success",
  "started_at": "2026-04-23T01:16:57.704Z",
  "completed_at": "2026-04-23T01:18:50.000Z",
  "tool_call_count": 15,
  "retry_count": 0,
  "tokens_input_total": 185929,
  "tokens_output_total": 11832,
  "estimated_cost_usd": 0.07014625,
  "files_changed": [
    { "path": "Shell/DefaultSkipDirectories.cs", "action": "created" },
    { "path": "Shell/GrepTool.cs", "action": "modified" }
  ],
  "scope_adherence": {
    "in_scope": true,
    "out_of_scope_paths": []
  },
  "tests": null,
  "acceptance": [
    { "item": "…", "status": "pass", "citation": "Shell/GrepTool.cs:14" }
  ],
  "sub_agents_spawned": [
    { "role": "closeout", "verdict": "pass", "notes": "…" }
  ],
  "notes": "…",
  "blocked_question": null,
  "rejection_reason": null,
  "worktree_path": "/home/.../nb.worktrees/T-005",
  "branch": "contract/T-005",
  "trace_path": "/home/.../nb.worktrees/T-005.trace/trace.jsonl",
  "transcript_path": "/home/.../nb.worktrees/T-005.trace/transcript.md"
}
```

### Field reference

| Field | Type | Always present | Source |
|---|---|---|---|
| `task_id` | string | yes | Parsed from contract header |
| `terminal_state` | `success` \| `failure` \| `rejected` \| `blocked` | yes | Executor control flow |
| `started_at` | ISO-8601 UTC | yes | `DateTime.UtcNow` at `Executor.RunAsync` entry |
| `completed_at` | ISO-8601 UTC | yes | `DateTime.UtcNow` at exit |
| `tool_call_count` | int | yes | `state.ToolCallCount` — across all phases |
| `retry_count` | int | yes | Always 0 in v1; reserved for future in-loop retries |
| `tokens_input_total` | long | yes | Sum of `response.Usage.InputTokenCount` across turns |
| `tokens_output_total` | long | yes | Sum of `response.Usage.OutputTokenCount` across turns |
| `estimated_cost_usd` | decimal | yes | `Pricing.Estimate(modelName, tokensIn, tokensOut)` |
| `files_changed` | array of `{path, action}` | yes (possibly empty) | `state.FilesTouched`; action ∈ `created` \| `modified` \| `deleted` |
| `scope_adherence` | `{in_scope, out_of_scope_paths}` | yes | Algorithmic: which `files_changed` paths aren't in contract Scope |
| `tests` | `{added, modified, existing_passed}` \| null | nullable | Unpopulated in v1 |
| `acceptance` | array of `{item, status, citation}` | yes (possibly empty) | Closeout's reports if run; else self-check's; else empty |
| `sub_agents_spawned` | array of `{role, verdict, notes}` | yes (possibly empty) | Currently only `closeout` entries |
| `notes` | string | yes (possibly empty) | Executor's final text + closeout demotion note if applicable |
| `blocked_question` | `{category, summary, offending_input}` \| null | nullable | Set when `terminal_state=blocked` |
| `rejection_reason` | string \| null | nullable | Set when `terminal_state=rejected` (validation failed) |
| `worktree_path` | string | yes (empty on reject) | Absolute path |
| `branch` | string | yes (empty on reject) | `contract/<task_id>` |
| `trace_path` | string | yes (empty on reject) | Absolute path to `trace.jsonl` |
| `transcript_path` | string | yes (empty on reject) | Absolute path to `transcript.md` |

### Terminal state semantics

- `success` — main loop completed AND closeout (if it ran) passed every Acceptance bullet.
- `failure` — either the executor gave up (tool-call budget, etc.) OR closeout demoted a would-be-success run by flagging a `fail` verdict. Read `acceptance[]` and `sub_agents_spawned[].notes` to distinguish.
- `rejected` — contract failed structural validation before executing. `rejection_reason` carries the specific failure.
- `blocked` — a safety gate fired, or the model explicitly couldn't proceed. `blocked_question` carries category / summary / offending input.

### `blocked_question.category` values

Defined in `BlockedCategory` enum (`BuildResult.cs:66`). Serialized snake_case.

| Category | Meaning |
|---|---|
| `clarify_then_retry` | Contract was missing info; add context and re-run |
| `revise_contract` | Contract was contradictory or wrong; rewrite |
| `rescope_or_capability` | Scope too wide or needed a capability (e.g. network); split or defer |
| `abandon` | Don't retry; system is asking for judgment (safety gates, doom-loop) |
| `transient_retry` | Provider/infra hiccup; re-run unchanged |

## Trace (JSONL, output)

Append-only per-line JSON sidecar at `<worktree>.trace/trace.jsonl`. Written by `TraceWriter` in `TraceWriter.cs`. Autoflush — an ungraceful exit preserves what was observed up to the crash.

Event types: `start`, `turn`, `tool_call`, `end`. Exactly one `start` and one `end` per run; 1-N `turn`s; 0-N `tool_call`s per turn.

### `start` event

Written once at `Executor.RunAsync` entry.

```json
{
  "type": "start",
  "version": "1",
  "timestamp": "2026-04-23T01:16:57.704Z",
  "task_id": "T-005",
  "title": "Consolidate SkipDirectories",
  "goal": "…",
  "provider": "AzureFoundry",
  "worktree_path": "/home/.../nb.worktrees/T-005",
  "branch": "contract/T-005"
}
```

### `turn` event

Written once per chat response.

```json
{
  "type": "turn",
  "timestamp": "2026-04-23T01:17:08.123Z",
  "n": 3,
  "duration_ms": 14012,
  "tokens_in": 8266,
  "tokens_out": 11595,
  "finish_reason": "ToolCalls",
  "had_tool_calls": true,
  "text": "…assistant visible text, truncated to 4000 chars with a '+N chars' suffix if longer…"
}
```

- `n` = turn number (1-indexed).
- `tokens_in` / `tokens_out` come from `response.Usage`. Nullable (some providers don't return usage).
- `finish_reason` = whatever the provider reported. `"ToolCalls"`, `"Stop"`, `"Length"`, `"ContentFilter"`, `"exception:<Type>"` on provider exceptions.
- `text` = `response.Text` truncated at 4000 chars. Captures assistant reasoning + visible output.

### `tool_call` event

Written once per dispatched tool call.

```json
{
  "type": "tool_call",
  "timestamp": "2026-04-23T01:17:10.456Z",
  "turn": 3,
  "call_id": "call_abc123",
  "name": "write_file",
  "args_hash": "1a2b3c4d5e6f",
  "args": "{\"path\":\"Shell/Foo.cs\",\"content\":\"…\"}",
  "success": true,
  "duration_ms": 8,
  "result_preview": "created Shell/Foo.cs (535 bytes)",
  "error": null
}
```

- `args_hash` = MD5 of the serialized args, truncated to 12 hex chars. Always present.
- `args` = full serialized JSON of the args. Present only for tools on the whitelist (`write_file`, `edit_file`, `apply_patch`, `bash`, `finish_work`) or when the call errored. `null` otherwise — for read-only tools we log the hash for loop detection but not the contents.
- `success` = result did NOT start with `"ERROR:"`.
- `result_preview` = first 200 chars of the result (non-error case).
- `error` = first 2000 chars of the result, populated only when `success=false`.

### `end` event

Written once in `Executor.RunAsync`'s `finally` block. Captures the final state (after all demotions, after closeout).

```json
{
  "type": "end",
  "timestamp": "2026-04-23T01:18:50.000Z",
  "terminal_state": "success",
  "tool_call_count": 15,
  "turn_count": 9
}
```

### Reading traces

- JSONL parsers are easy; `jq` works. Example: `jq 'select(.type == "tool_call" and .success == false)' trace.jsonl` to find every failed tool call.
- Trace is forensic-grade. Tool results aren't logged in full (the worktree diff tells that story) — the trace carries *signal* (what was tried, how long it took, what failed).
- Don't rely on trace files written before a feature landed. The `start.version` field exists for future schema migrations but hasn't been bumped yet — if the shape changes, it will.

## Transcript (markdown, output)

Human-readable render of the JSONL trace at `<worktree>.trace/transcript.md`. Written by `TranscriptRenderer.Render` in `TranscriptRenderer.cs` at end-of-run, after `trace.WriteEnd`.

Also regenerable from a standalone JSONL file via `dotnet run -- --render-transcript <path>` — useful for older traces or iterating on the renderer.

Structure:

```markdown
# T-NNN — contract title

**Goal:** …

| | |
|---|---|
| Started | 2026-04-23 01:16:57 UTC |
| Completed | 2026-04-23 01:18:50 UTC |
| Duration | 1m 52s |
| Terminal state | **success** |
| Provider | AzureFoundry |
| Worktree | `/path/to/…` |
| Branch | `contract/T-005` |
| Turns | 9 |
| Tool calls | 15 |
| Tokens | 185,929 in / 11,832 out |

---

## Turn 1 — 4.4s — 2,388 in / 570 out

**read_file** — 3ms — `args#9aca565a9f46`

_result:_
```
…first 200 chars of the result…
```

## Turn 2 — 17.5s — 5,228 in / 2,760 out

> (any assistant text appears here as a blockquote)

**todo_write** — 6ms — `args#1c67399134bd`

_result:_
```
…
```
```

Rendering rules:

- Assistant text appears as a blockquote.
- Tool calls show name + duration; args as a code block if the trace recorded them (whitelist tools), otherwise just the args hash.
- Results are truncated to 200 chars with a `…[+N chars]` suffix. Errors use 2000 chars.
- Failed tool calls marked `**failed**` in the header.

The transcript is a convenience view — the JSONL is the source of truth. A consumer writing scripts should read the JSONL; the transcript is for humans skimming.
