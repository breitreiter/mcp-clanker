# Research: shape of the build executor (v1)

Nail down the shape of `build(contractPath)` before writing it, so v1 is
something we can measure against rather than something we iterate fixes
into. Target: a v1 that can grind a deliberately-trivial contract
end-to-end against Azure Foundry / gpt-5.1-codex-mini.

## What nb teaches us

**The tool-call loop is small.** `ConversationManager.cs` does it in ~80
lines of core logic: invoke `IChatClient.GetResponseAsync`, check for
`FunctionCallContent` in the response, execute each tool, append the
results as a `ChatRole.Tool` message, recurse. Termination = no tool
calls in the last response, tool-call budget hit, or tool-failure budget
hit. We copy this shape verbatim and strip nb's interactive approval
and history-compaction for v1.

Key pointers: `/home/joseph/repos/nb/ConversationManager.cs:116–692`.

**Tools are just `AIFunctionFactory.Create(delegate, name, description)`.**
The delegate's parameter names + types become the JSON schema the model
sees. Shove them into `ChatOptions.Tools` as a `List<AITool>`. No
registry, no interface, no scaffolding.

**System prompt lives in the history list at index 0.** Not passed
separately via `ChatOptions`.

**Reasoning content is NOT round-tripped in nb.** nb never handles
`TextReasoningContent`; if the SDK surfaces it, nb silently drops it on
the next turn. For gpt-5.1-codex-mini via the Responses API this might
be fine (the SDK may round-trip state internally) or might cause subtle
drift across turns. We saw `TextReasoningContent` in our single-turn
ping. Behaviour across turns is **untested** and flagged by the research
pass as the biggest unknown before we commit.

**Shell tools worth copying nearly verbatim:**
- `Shell/WriteFileTool.cs` (~80 lines)
- `Shell/ListDirTool.cs` (~60 lines)
- `Shell/ReadFileTool.cs` (~165 lines, but strip PDF + image handling)

**Shell tools worth studying before copying:**
- `Shell/BashTool.cs` (real output-truncation "sandwich" logic)
- `Shell/EditFileTool.cs` (CRLF normalisation, uniqueness validation)
- `Shell/GrepTool.cs` (multiline regex modes)

**Things tightly coupled to nb's Spectre.Console CLI (must rewire or
drop for headless):**
- `BashTool`'s AnsiConsole.MarkupLine status output
- `CommandClassifier`'s danger detection (only matters for approval UX)
- `ShellEnvironment`'s system-prompt builder (we'll write our own)

**Approval.** nb prompts interactively by default. For headless MCP,
`_trustMode = true` + a pattern list is the path. Or simpler still for
v1: auto-approve everything whose touched paths fall inside the
executor's working directory. The executor is the only caller and its
contract is trusted input.

## Framing: `blocked` is the return channel, not an error mode

Opus writing contracts detailed enough to never fail would mean Opus
writing the code. The whole point is that Opus yolo-writes "this seems
like a reasonable chunk" contracts, the executor grinds, and when
something isn't right the contract bounces back. `blocked`-class
outcomes are expected, not exceptional.

The system's job isn't to minimize them — it's to make the blocked →
fix → retry cycle cheap. Analogous to how Claude's code-yolo works
because of fast-precise-bounded-reversible feedback (compiler errors,
tests, git reset). Our "strong types + tests" equivalents:

| Code | Contracts |
|---|---|
| Strong types | Explicit `Scope:`, `Non-goals:`, bash whitelist |
| Fast compile | Contract validator — fails before execution |
| Precise error | `blocked_question.summary` — actionable enough to diff the contract |
| Tests | Closeout sub-agent (v2) — independent filesystem inspection |
| Bounded radius | Tool-call budget, doom-loop detector, scope enforcement |
| Free rollback | **Git worktrees** — see execution environment below |
| Errors fire early | Doom-loop and whitelist trigger *before* 100 wasted tool calls |

## Execution environment: one worktree per contract

**Target repo inferred from Claude Code's cwd.** When `build()` is
invoked, the cwd of the MCP server process (= Claude Code's cwd) is
the target repo. No per-contract `**Target:**` field, no config
override for v1 — add one later if cross-repo work turns out to be
a real use case. Worst case today: user is working outside a git
repo and the worktree step fails loudly.

Each `build(contractPath)` invocation creates a fresh git worktree
on a new branch in the target repo before invoking the executor:

```
cd <target-repo>
git worktree add ../<target-repo>.worktrees/T-042 -b contract/T-042
```

The worktree path becomes the executor's `workingDirectory`. All
mutations (writes, edits, bash commands) happen inside it. The main
checkout is untouched during execution — the parent (Opus / human)
can keep navigating, grepping, reading without stepping on the child.

**Lifecycle for v1:**
- On **success**: leave the worktree + branch. Parent decides whether
  to merge and `git worktree remove`.
- On **blocked / failed / rejected**: leave the worktree so parent can
  diff it, read it, maybe hand-edit and retry. Cleanup is manual.
- No automatic merge. Ever. Explicitness is cheap and safer.

**Benefit beyond rollback:** parallelism becomes a door we haven't
closed. Multiple contracts can run concurrently in their own worktrees
later, without v1 having to design for it now.

**Gotcha:** anything that wants to reference "the main repo" (repo map
generator in v2, cross-worktree diffs) needs the main-repo path as a
separate input, not inferred from `workingDirectory`.

## Proposed v1 shape

One file: `Executor.cs`.

```csharp
public static async Task<BuildResult> RunAsync(
    IChatClient chat,
    Contract contract,          // parsed contract
    string workingDirectory,    // worktree path
    int maxToolCalls,           // from config, default 500
    CancellationToken ct);
```

The worktree is created by the `build()` MCP tool *before* calling
`RunAsync`, and `workingDirectory` is that worktree path. The executor
itself is worktree-ignorant — it just mutates whatever directory it's
given. Keeps the executor simple; keeps the worktree-management code
where git-ignorant executors can't break it.

Inside:

1. Build a system prompt containing: the full contract body, a short
   "call tools until done, then stop" guidance block, and a
   placeholder repo-map section (empty for v1).
2. Build the tool list: `bash`, `read_file`, `write_file`, `edit_file`,
   `grep`, `list_dir`, `todo_read`, `todo_write`.
3. Seed history: `[System, User: "Begin."]`.
4. Run the recursive loop. Track tool-call count. On each `write_file`
   / `edit_file` / `bash` mutation, record the path into a files-touched
   manifest for proof-of-work. Run a doom-loop detector on each turn —
   bail with terminal state `blocked` if triggered (see below).
5. When the model stops calling tools, ask it (one final turn) to emit
   a short JSON object with `{ notes, blocked_question, rejection_reason }`.
   Merge into the harness-observed fields, return `BuildResult`.

**File-system trust:** auto-approve any tool call whose touched paths
resolve under `workingDirectory`. No interactive prompts. Paths outside
→ the tool returns a failure string the model can see and react to.

**Bash permissions.** No static command-prefix whitelist — that
approach is too brittle across project types (every non-.NET
contract would false-positive in the first 5 tool calls) and doesn't
stop the attacks that actually matter (malicious packages pulled via
commands that *would* be whitelisted). Instead, two narrower gates:

1. **Danger-pattern detection** — copy nb's `CommandClassifier`
   verbatim (261 lines). Blocks `rm -rf /`, `sudo`, `shutdown`,
   forkbombs, `dd` to raw devices, etc. These are lines that have no
   legitimate reason to be crossed by a rote-coding executor. Hit =
   terminal state `blocked`, category `abandon`.

2. **Network egress restriction** — `curl`, `wget`, `nc`, `ssh`,
   `scp`, `rsync` (to remote hosts), `gh api` with write verbs all
   route through a checker that denies by default. Exceptions:
   - Localhost / `127.0.0.1` — always allowed (hitting local dev server)
   - Per-contract `**Allowed network:**` section listing hosts the
     contract explicitly authorizes (e.g., `github.com` for a
     PR-creation contract)

   Hit = terminal state `blocked`, category `rescope_or_capability`.

Everything else runs. Package managers (`npm install`, `pip install`,
`dotnet restore`, `cargo build`) stay on — supply-chain risk is
accepted, same risk we take every time we `git clone`.

**Secret-path redaction** on `read_file`: paths under `~/.ssh/`,
`~/.aws/`, `~/.config/gh/`, `.env*` files return a redacted marker
unless the contract explicitly names them in `Scope:` or `Context:`.
Defense in depth against "model reads a secret then exfiltrates it"
— layered with network restriction, not replacing it.

**v2 gate: Docker sandbox.** v1 runs unsandboxed, on the host. This
is the trust boundary: work-use of mcp-clanker is deferred until we
have container-isolated execution. v2 adds `docker run` around the
executor with `--network=none` (or restricted bridge), worktree bind
mount, resource limits. Kills the whole permissive-vs-strict
argument for shell — inside a container, let the model run anything.

**Doom-loop detector.** Copy nb's pattern (`_doomLoopDetector` and
`_errorTracker.LimitReached()` at `ConversationManager.cs:673`).
Triggers for v1:
- Same tool + same primary-arg-hash called N times in a row (e.g.
  N=3) — model is spinning.
- M consecutive tool failures (e.g. M=5) across any tools — model is
  thrashing.
- K consecutive denied-network-egress attempts (e.g. K=2) — model
  has decided it needs network the contract didn't grant; stop
  asking and escalate.

Trigger → terminate loop with `terminal_state = blocked`, fill
`blocked_question` with a harness-generated summary of what tripped.

Danger-pattern hits are *immediate* block (no N=2 threshold) — they
shouldn't happen once in a rote-coding contract.

**Deferred to v2:** closeout sub-agent, spawn tool, repo map generator,
acceptance-item verification, test-runner integration, history
compaction, session resume, **Docker sandbox (blocks work-use until
this lands)**.

**Contract parsing v1:** regex over the markdown template. `**Goal:**`,
`**Scope:**`, `**Contract:**`, `**Context:**`, `**Acceptance:**`,
`**Non-goals:**`, `**Depends on:**`. Fail loud on missing sections
(terminal state `rejected`, no LLM needed).

**Proof-of-work v1:** hybrid. **Wire format: JSON.** The MCP `build()`
tool returns the serialised JSON string matching the brief's schema,
with v2-gated fields set to their empty sentinel (null / `[]`). JSON
preserves the real structure (nested `files_changed[]`, object-typed
`blocked_question`, etc.) and Claude reads it fine. Add a markdown
summary header later if the JSON-in-terminal UX becomes annoying;
don't gild now.
- Harness-observed: `started_at`, `completed_at`, `tool_call_count`,
  `files_changed[]` (paths + action, not line counts yet),
  `terminal_state`, `worktree_path`, `branch`, doom-loop trigger
  reason when applicable.
- Model-declared (final turn): `notes`, `blocked_question.summary`,
  `rejection_reason`.
- Not produced: `tests.*`, `acceptance[]`, `sub_agents_spawned`,
  `retry_count`, `lines_added/removed`. These come in v2.

**Trace (forensic sidecar, not primary artifact).** Proof-of-work is
what the orchestrator reads. The reviewer (v2) reads the diff. Humans
read the worktree + proof-of-work. Almost nobody routinely reads the
trace — it exists for the ~5% of contracts that go weird enough to
warrant dig-in. Design accordingly: optimise for skim when everything's
fine, forensic-ability when suddenly turn 82 is the most important
turn of the day.

Key insight: **the worktree is part of the archaeological record.**
File reads don't need content logged — the files are still in the
worktree. Mutations don't need content logged — `git diff` has them.
This cuts trace size by 10–100x vs "log every tool call verbatim."

What the trace stores:

- **Per-turn summary** (~100 bytes): tokens in/out, duration,
  finish-reason, whether tools were called.
- **Per-tool-call** (~50 bytes typical): name, args-hash,
  success/fail, duration. Not args or result content, except:
  - Mutations (`write_file`/`edit_file`/`apply_patch`) log args for
    audit; results still derivable from worktree diff.
  - `bash` logs command text + exit code.
  - Errors log full payload.
- **Gate fires (doom-loop, danger-pattern, denied-network):** full
  context — what triggered, recent tool-call history, state-machine
  snapshot.

Target size: ~50 KB for a 100-turn contract. JSONL in a per-contract
sidecar directory. Compressible if we ever care.

**`blocked_question.category`** — organised around what the **parent
does next**, not what triggered it. The trigger is recoverable from
the trace; the category is what Opus reads to decide action without
loading the trace.

| Category | What the parent does |
|---|---|
| `clarify_then_retry` | Contract is fine, just ambiguous. Parent answers the question, resubmits. |
| `revise_contract` | Contract is structurally wrong (missing scope file, contradictory acceptance, over-broad). Edit + retry. |
| `rescope_or_capability` | Task needs a tool we didn't grant, or a file outside scope. Parent decides: grant, narrow, or drop. |
| `abandon` | Doom-loop / repeated failures. Signal: the work wasn't actually rote. Human or parent does it directly. |
| `transient_retry` | SDK error, tool crash, flaky process. Just try again. |

Trigger-to-category mapping: doom-loop → `abandon`, danger-pattern
hit → `abandon`, denied-network-egress → `rescope_or_capability`,
model self-stop with question → `clarify_then_retry`, validator fail
→ `revise_contract`, HTTP 5xx / tool crash → `transient_retry`.

## Decisions

- **Q1 — User-turn seed.** System prompt holds the contract. First user
  message is just "Begin."
- **Q2 — Trust model (v1, unsandboxed).** Files: auto-approve inside
  `workingDirectory` (the worktree); deny outside; redact secret
  paths (`~/.ssh`, `~/.aws`, `.env*`) on read. Bash: no command
  prefix whitelist. Two narrow gates — nb's `CommandClassifier`
  blocks danger patterns (`rm -rf /`, `sudo`, etc.) as hard-abort;
  network egress tools (`curl`, `wget`, etc.) deny by default with
  per-contract `**Allowed network:**` override. Package managers on.
  **v2 gate: Docker sandbox is required before work-use.**
- **Q3 — Contract format.** Markdown + regex for v1. Move to YAML /
  other structured format only if we find a successful pattern in the
  wild that relies on it.
- **Q4 — Proof-of-work split.** Hybrid for v1 (harness observes
  mechanical fields, model emits narrative fields). Second-pass LLM
  verification becomes the closeout sub-agent in v2.
- **Q5 — Tool set.** Proposed: `bash`, `read_file`, `write_file`,
  `edit_file`, `grep`, `list_dir`, **`todo_read`, `todo_write`**.
  GPT-5.x relies heavily on the todo / todo-rescue pattern and will
  likely fail without it. Dropped for v1: `fetch_url`, `find_files`.
- **Q6 — Model coverage.** Nice to have, not on the critical path. Try
  it if cheap; don't block v1 on it.
- **Q7 — Exit / resume.**
  - **Resume:** skipped for v1. Each `build` starts fresh.
  - **Give-up path:** v1 MUST support abandoning a task cleanly and
    returning a structured exception state (terminal state `blocked`
    with a `blocked_question` the parent can act on).
  - **Doom-loop preventer:** v1 requirement, not v2. Design above.
  - **Open for later:** when the parent (Claude Code) receives
    `blocked`, does it (a) inject guidance and resume the child, or
    (b) rewrite the contract from scratch using what was learned?
    This is a Claude-Code-skill design question, not an executor
    question. Park until we have real blocked cases to reason about.
- **Q8 — Target repo.** Inferred from Claude Code's cwd (= the MCP
  server process's cwd). No contract-level or config override in v1.
  Add an override if cross-repo work turns out to be real usage.
- **Q9 — Proof-of-work wire format.** JSON string matching the
  brief's schema, v2-gated fields as empty sentinels.

## Model-native patterns from Codex (document so we don't fight the model)

gpt-5.x has been trained under specific patterns by OpenAI/Codex.
Where we align with them, we get quality for free; where we diverge,
we pay for it in retry cycles and malformed output. These notes
capture what to mirror.

Primary sources:
- [Unrolling the Codex agent loop — OpenAI](https://openai.com/index/unrolling-the-codex-agent-loop/)
- [Codex Prompting Guide — OpenAI Cookbook](https://cookbook.openai.com/examples/gpt-5/codex_prompting_guide)
- [Codex Subagents](https://developers.openai.com/codex/subagents)
- [Apply Patch tool guide](https://developers.openai.com/api/docs/guides/tools-apply-patch)
- [Codex CLI Features (reviewer)](https://developers.openai.com/codex/cli/features)

### v2 closeout: mirror Codex's reviewer pattern

The single most important thing to get right so we don't fight the
model. Codex has a built-in reviewer; gpt-5.x expects reviews to
look like this:

- **Input: the diff, not the full execution trace.** The reviewer
  reads the patch that was produced, not the tool-call log or the
  conversation history. Fresh-ish context by construction.
- **Tools: read-only.** Can read files, can run tests, can grep.
  Cannot mutate. Cannot re-execute the task. Review produces
  *findings*, not *fixes*. Codex quote: *"a dedicated reviewer that
  reads the diff you select and reports prioritized, actionable
  findings without touching your working tree."*
- **Output: specific findings with line references.** Each issue or
  acceptance item cites the hunk it refers to. No blanket "looks
  good." Empty findings = pass; populated = fail with evidence.
- **Same model by default.** Codex uses the session model unless
  `review_model` is explicitly configured. Our default: executor's
  model. Config override for cross-family validation when we want
  it. Don't burn Anthropic budget by default.
- **Invocation: automatic in our design (vs user-triggered in
  Codex).** Codex is a human-in-loop IDE tool; we're an async
  hand-off system. Automatic closeout is right for us — this is
  the one deliberate divergence.

### v1 decision point: `apply_patch` as the mutation channel

OpenAI's direct claim: *"the model has been trained to excel at
this diff format."* `apply_patch` is a unified-diff-with-envelope
tool exposed as a first-class Responses API tool:

```
*** Begin Patch
*** Update File: path/to/foo.ts
@@ def some_function():
-    old line
+    new line
*** End Patch
```

If we adopt it, one tool replaces nb's `WriteFileTool` (whole-file)
and `EditFileTool` (exact-string-match). Tradeoffs:

- **Pro:** model-native → less malformed output, fewer retry cycles
- **Pro:** one mutation tool, not two
- **Pro:** produces actual diff hunks we can log into the execution
  trace — natural artifact for the reviewer to read later
- **Con:** patch application logic is more involved than whole-file
  write
- **Con:** nb's tools are proven in production; `apply_patch` is
  new for us

Decision at code-writing time. Default assumption: **adopt
`apply_patch`** unless there's a specific reason to keep nb's tools.
This changes our v1 tool set to: `bash`, `read_file`, `apply_patch`,
`grep`, `list_dir`, `todo_read`, `todo_write` (seven tools, not
eight).

### v2+: server-side history compaction via Responses API

Codex offloads history compaction to `/responses/compact`, which
returns an `encrypted_content` item preserving latent state
server-side. If Azure Foundry's Responses API exposes this
endpoint, compaction is free and we skip porting nb's manual
history-compaction code entirely.

Action: when history-compaction becomes relevant (long-context
contracts, not v1), check Azure's Responses API docs for the
compact endpoint before porting nb's logic.

### Confirmed: we are architect/editor, not CIV

Codex itself is a single-agent loop with planning-as-a-tool-call
(`update_plan`), in-band verification (tests run as shell calls),
and an opt-in reviewer/subagents layered on top. It is explicitly
*not* Coordinator/Implementor/Verifier. This is the shipping
OpenAI pattern; our executor operates in that family.

Our overall system is architect (Claude Code plans) + editor (nb
grinds) + reviewer (v2 closeout). Not CIV. Drop CIV framing when
reading the brief.

## Research I'll do before writing code (unprompted)

1. **Multi-turn tool-calling smoke test against AzureFoundry.** Extend
   the `--ping` harness to register one trivial tool (e.g., `echo(x) → x`),
   prompt the model to call it, feed the result back. Verify the second
   turn succeeds. Removes the biggest reasoning-token unknown before we
   commit to the shape.
2. **Inspect the specific shell tool files we plan to copy** — note
   what to keep vs strip (PDF/image handling in ReadFileTool, Spectre
   calls in BashTool).
3. **Check `AIFunctionFactory.Create` behaviour with async delegates**
   (`Func<..., Task<string>>`). Matters for bash and any I/O tool.

Once those three are verified: I write `Executor.cs`, `Contract.cs`
(parser), copy + adapt the six tools, wire `build()` to `Executor`,
run against a deliberately-trivial contract ("create file X with
content Y, run `cat X`"). That's the 80% moment — after which we have
data and can measure, rather than vibes.

## TL;DR for skimming

- `blocked` is the return channel, not an error mode. System optimizes
  for cheap blocked → fix → retry cycles, not for minimizing blocked.
- One worktree per contract (`git worktree add ... -b contract/T-NNN`).
  Main repo stays clean; rollback = `git worktree remove`; parallel
  execution is a future door left open.
- Loop shape: known, copy from nb.
- Tools: likely 7 of them — `bash`, `read_file`, `apply_patch`,
  `grep`, `list_dir`, `todo_read`, `todo_write`. Default to
  `apply_patch` over nb's `write_file` + `edit_file` because
  gpt-5.x is trained on it; decide at code-writing time.
- Contract parser: regex + markdown, v1.
- Proof-of-work: hybrid harness/model-declared, v1 skips acceptance + tests.
- Trace: forensic sidecar, not a universal intermediate. Proof-of-work
  is primary. Worktree holds content; trace holds signals. Target
  ~50 KB per 100-turn contract.
- No bash whitelist. Two narrow gates: `CommandClassifier`
  danger-pattern blocks + network-egress restriction (with per-contract
  allowlist). Package managers run freely; supply-chain risk accepted.
- Docker sandbox is the v2 gate for work-use. v1 runs unsandboxed on
  the host — home-workstation only.
- Doom-loop preventer is v1, not v2. `blocked_question.category`
  organized around parent action (`clarify_then_retry`, `revise_contract`,
  `rescope_or_capability`, `abandon`, `transient_retry`), not trigger.
- Closeout, spawn, repo map, resume, acceptance verification: deferred to v2.
- v2 reviewer pattern comes from Codex: diff-only input, read-only
  tools, same-model-by-default, findings-with-line-references. Mirror
  this so we don't fight the model.
- Biggest unknown: reasoning-token round-trip behaviour. Cheap to
  de-risk with a tool-calling smoke test before writing Executor.cs.
