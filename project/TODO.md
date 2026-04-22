# TODO

Rolling list of work ahead. Priority-ordered within each section; not a
strict sequence. Goal: don't pause for review on each item — pull from
the top when it's time to do more.

## v1 — next work

**0. Author Claude Code skill + uplift MCP descriptions (paired; blocks v2-plan phase 2).** *(done — `skills/clanker.md` in repo, install instructions in README; `McpTools.Build` description flipped from ALPHA-deterrent to working. Iterate the skill content as phase 2–5 failure modes surface.)*
- Skill at `.claude/skills/clanker.md` (or wherever skills live for a project-tied trigger). Scope:
  - When to delegate via `build()` vs doing the work in-context (rote, well-scoped, model-family-aligned)
  - How to structure a contract using `template://contract`
  - How to interpret proof-of-work terminal states + `blocked_question.category`
  - The retry / rewrite loop (blocked → read POW + maybe trace → revise contract → retry)
  - Cost framing: why this exists (cheap-executor delegation, preserving Opus budget)
- Flip `McpTools.Build` description from current ALPHA-warning string to a working one *in the same change* so the two stay in sync.
- Iterative — draft minimal, tune based on phase 2–5 failure modes rather than trying to foresee them.

**1. Scope-adherence check.**
- Algorithmic, no LLM: if any `files_changed` path is outside contract `Scope:`, flag it
- Surface in proof-of-work as a boolean + list of out-of-scope paths
- ~1 hour, cheap real quality signal

**2. Safety gates.**
- Port nb's `CommandClassifier` (261 lines) → catches `rm -rf /`, `sudo`, `shutdown`, forkbombs, etc. Immediate `terminal_state=blocked`, category `abandon`
- Network-egress checker: `curl`/`wget`/`nc`/`ssh`/`scp`/`rsync`/`gh api` (write verbs) denied by default; localhost exempted; per-contract `**Allowed network:**` override
- Doom-loop detector: N=3 same-tool-args in a row; M=5 consecutive failures; K=2 consecutive denied-network attempts
- Trigger → `blocked` with appropriate category from the `blocked_question.category` table

**3. `apply_patch` tool.**
- Replace `write_file` + `edit_file` with the model-native unified-diff-with-envelope format
- gpt-5.x is specifically trained on this; quality lift expected
- Use Responses API's built-in if available, otherwise a small parser

**4. Multi-provider write-surface: one tool or two? (research + test, blocks #3 only if we run Anthropic in v1.)**
- gpt-5.x is explicitly trained on `apply_patch` (OpenAI: *"the model has been trained to excel at this diff format"*). Anthropic's training story for diff formats is not published; Claude Code uses an exact-string-match `Edit` tool, presumably what Sonnet is tuned for. Aider ships 5 edit formats partly because one-size-fits-all loses quality.
- Options in roughly ascending complexity:
  - **Single tool (`apply_patch`) everywhere.** Accept Anthropic quality penalty. Cheapest; likely fine for v1 since primary executor is gpt-5.1-codex-mini.
  - **Offer both, hint in system prompt to pick.** Relies on the model to route; models sometimes ignore tool-choice hints. Risky.
  - **Two tools, per-provider tool construction.** `apply_patch` for OpenAI/Azure; `edit_file` (exact-string-match, nb-style) for Anthropic. Hide the wrong one per provider at tool-construction time. One executor code path; different tool dicts. Ugly but bounded.
- This is a measurement question, not a design question. Research pass (do Anthropic docs or third-party writeups say anything concrete about Claude + unified-diff?) plus a targeted experiment once we have a medium-complexity contract: run the same contract with apply_patch against Claude Sonnet vs gpt-5.1-codex-mini, compare traces and diff quality.
- Skip entirely if we're Azure-only for v1; pick up as a precursor to serious multi-provider work in v2.

**5. Remaining tools.**
- `grep` (port from nb, strip PDF/image bits)
- `list_dir` (port from nb, trivial)
- `todo_read` / `todo_write` — critical for gpt-5.x which relies heavily on todo + todo-rescue pattern

**6. Remaining MCP handlers (currently stubs).**
- `list_tasks()` — walk contracts dir, return `[{id, title, state}]`
- `get_contract(taskId)` — read contract file
- `get_log(taskId)` — read trace sidecar
- `validate_contract(contractPath)` — run parser + validator, return result, don't execute
- `update_contract(contractPath, content)` — write a contract file
- Blocks: need contract location convention (see hygiene section)

**7. Rendered human transcript.** *(done — `TranscriptRenderer.cs`, written to `transcript.md` next to `trace.jsonl` at end-of-run. Also regenerable via `dotnet run -- --render-transcript <trace.jsonl>`.)*
- Markdown alongside JSONL trace
- "Turn 1: model said X, called bash(ls), result was Y (truncated)..."
- For the ~5% of runs where something's weird and the human needs to read

**8. Light acceptance self-check.**
- One terminal-time model turn: "for each acceptance item, pass/fail with citation to a specific line/file in the diff"
- Cheap; gives *some* quality signal before v2's full closeout sub-agent lands
- Model can still bluff but has to point at something specific

**9. Token + cost estimation.**
- Sum `response.Usage.InputTokenCount` / `OutputTokenCount` across turns
- Add `tokens_input_total`, `tokens_output_total` to proof-of-work
- Dumb lookup table by model name → per-MTok rate (input/output separate), hardcoded, accept staleness
- `estimated_cost_usd` field computed from lookup + usage
- Naturally hooks into the trace, which already captures per-turn usage
- Useful even at N=1 ("this contract used 8K tokens and cost $0.03"); aggregates into batch stats later
- Answers the load-bearing question: is this system actually saving money vs just running Opus?

**10. File tool polish — match nb patterns on existing tools.**

Small quality fixes to bring our bash/read_file closer to nb's shape, from a diff of nb's Shell/ vs our Tools.cs:

- `read_file`: prepend 1-based line numbers to each output line (`"1: foo\n2: bar\n..."`). Helps the model plan and reference edits.
- `read_file`: default `limit=2000` when not specified. Prevents a single big read from blowing context.
- `bash`: replace flat half+marker+half truncation with nb's sandwich (head 50 lines + `"[N lines omitted (M KB)]"` + tail 20). Error info clusters at the end; flat split can bisect the interesting part.
- `bash`: UTF-8 sanity check — return `"Error: Binary output detected"` rather than garbled text when output contains `�`.
- `bash`: capture the `description` parameter into the trace (currently accepted but unused; trace already captures the rest of the call).
- `read_file` (and any other tool with optional params): `int? offset` / `int? limit` are parsed as *required* by `AIFunctionFactory` despite being nullable. Model has to guess values or get an `ArgumentException` on first attempt. Fix: add `= null` defaults to the parameter declarations so MEA's schema generator marks them optional. Discovered via trace of a T-003 run.
- `bash`: optional per-call timeout with a configured cap (nb default 30s, per-call up-to-cap). Low priority — 120s blanket is fine until we see need.

**11. Cosmetic JSON.**
- Add `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` to `BuildResultJson.Options` so backticks and smart quotes don't render as ``` / `“`

## v1 hygiene / known gaps

- **Remove `build(targetRepo)` dev escape hatch before v2/shipping.** Added 2026-04-22 during phase 2 validation to let one Claude Code session target a different repo than its cwd. Production flow is one Claude Code session per target repo — the param is a weird surface in that world (wrong-repo footgun, injection vector, confuses the mental model). Keep while validating v2 against multiple repos from a single session; delete before shipping. Guards in place: absolute path, existing dir, has `.git` entry. See `ResolveTargetRepo` in `McpTools.cs`.
- **Contract location convention.** Currently `build()` takes any path; no convention. Proposal: `<target-repo>/contracts/T-NNN-slug.md`. Decide and document when we implement `list_tasks`.
- **Worktree cleanup tool.** Right now parent must `git worktree remove --force` + `git branch -D` manually. Worth an MCP tool (`cleanup_contract(taskId)`) once we have real contract volume.
- **Worktree snapshot semantics.** Worktree is created from HEAD — uncommitted work in the main checkout is invisible to the executor. Document this in CLAUDE.md so future-me doesn't get confused.
- **`retry_count` is always 0 in v1.** Keep the field for schema stability; fill it when v2 retry lands.
- **Windows support: port nb's shell detection.** Current `Tools.cs` hardcodes `/bin/bash`; works on Linux/most macOS, hard-breaks on Windows. Port nb's `ShellEnvironment.DetectShell()` + `FindGitBash()` verbatim: searches `C:\Program Files\Git\bin\bash.exe` (+ x86 + PATH) on Windows, falls back to `$SHELL` / `/bin/sh` on Unix, throws a helpful "install Git for Windows" error if bash isn't found. Also port nb's Windows-only system-prompt note (*"The bash tool runs Git Bash (MSYS2). Use POSIX syntax — NOT PowerShell cmdlets or switches like -Force"*) — without it, models mix bash/PowerShell idioms and produce broken commands. Pairs with the rebuild-file-lock gotcha already documented in `CLAUDE.md`; same Windows story.

## v2 deferred

- **Closeout sub-agent.** Diff-only input, read-only tools, same-model-by-default, evidence-forced findings. Codex reviewer pattern. See `executor-v1-research.md` § "v2 closeout: mirror Codex's reviewer pattern".
- **Spawn tool / sub-agents.** Fan-out, not pipeline. `max_depth=2` per brief. Fresh context per spawn.
- **Repo map generator.** Static analysis + cached one-liners. Seeded into system prompt for orientation.
- **Session resume.** Restart from `<task-id>.state.json` sidecar. Needs at least one real failure-mode before we design against it.
- **Docker sandbox.** Work-use gate. v1 runs unsandboxed on the host (home only). See `executor-v1-research.md` § "v2 gate: Docker sandbox".
- **History compaction.** Check if Azure Foundry Responses API exposes `/responses/compact` (Codex pattern). If yes, ~free. If no, port nb's manual compaction.
- **Retry semantics.** Augment numbers as a starting point: 3 attempts per subtask, 5-iteration cap on replanning. Only relevant once v2 closeout can bounce work back in-loop.
- **External MCP client support.** Clanker is MCP-server-only today. No equivalent of nb's `mcp.json` + `McpManager.cs` that plugs third-party MCP servers' tools into the executor's chat session. Low priority — user doesn't use MCPs much in practice (Figma was the main one, moving away). Don't design anything that actively prohibits it; keep tool assembly in `Tools.cs` open to concatenation from external sources. Pick up when/if we see a real contract benefit (e.g., a docs-search MCP that materially helps rote-coding work).

## Measurement (needs data first)

- Batch stats. Tool-call histograms, duration distribution, terminal-state fractions across runs. Meaningful only at N>1; not actionable yet.
- Per-provider prompt variants. Only author when we actually run on non-AzureFoundry providers and see quality degrade.
- Golden contract set. Small/medium/deliberately-vague/deliberately-contradictory contracts for CI comparison. From `BRIEF.md` § Testing strategy.
