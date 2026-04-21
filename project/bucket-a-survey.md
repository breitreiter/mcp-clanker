# Bucket A — Survey of shipping hand-off artifact patterns

_Snapshot date: 2026-04-21. If redone later, append a new dated section rather than overwriting — the point of this doc is to capture what the field looked like at a moment, not to track a moving target._

## How this was produced

A primary-source-leaning web survey of how real shipping systems
structure the "planning-model hands a unit of work to an executing-model"
pattern. Deliberately **not** a synthesis — the goal was to map the
irreducible disagreements, not converge on a "best pattern." Source
weighting was enforced:

- **HIGH weight** — shipping products with real users suffering daily
  (Kiro, Cursor, Copilot Workspace, Aider, Devin, Factory, OpenHands,
  Sweep, Replit, Jules, Windsurf, Goose, Antigravity, Claude Code,
  Spec Kit)
- **MEDIUM weight** — academic papers with citation rep or shipping
  analog (SWE-agent, OpenHands SDK paper, Augment CIV guide)
- **LOW / skipped** — influencer blog posts, grad-student "solved
  agentic software engineering" papers without citation track record,
  marketing fluff

---

## HIGH-WEIGHT SOURCES

### GitHub Spec Kit

- **What it is:** GitHub's official spec-driven-development toolkit. Markdown templates + slash commands. Real adoption; lots of reported failure modes.
- **URLs:** https://github.com/github/spec-kit ; https://github.com/github/spec-kit/blob/main/templates/tasks-template.md ; https://github.com/github/spec-kit/blob/main/templates/commands/tasks.md
- **Contract shape:** Plain Markdown, no YAML. Workflow is `Constitution → Specify → Plan → Tasks → Implement`. The Tasks template uses a *strict* line-level format the generator enforces:
  > `- [ ] [TaskID] [P?] [Story?] Description with file path`

  Examples from the template:
  > `- [ ] T005 [P] Implement authentication middleware in src/middleware/auth.py`
  > `- [ ] T012 [P] [US1] Create User model in src/models/user.py`

  `[P]` means parallel-safe (different files, no dependencies); `[US1]` ties a task to a user story from `spec.md`. Phases: Setup → Foundational → User-story phases → Polish.
- **Proof-of-work:** The checkbox itself. No separate completion artifact — tasks are marked `[x]` in-place. This is the mechanism users report as breaking (see disagreements section).
- **Terminal states:** Implicit — phase-checkpoint driven. No explicit success/failure/blocked states on the task. The template relies on "Test independently" language before advancing phases.
- **Closeout/verification:** Not enforced by the toolkit. A separate `/speckit.review` command was requested as an issue (#1323) and is not core.
- **Scope controls:** File path embedded in task description; `[P]` gates parallelism by "different files" heuristic.
- **Retry:** Not defined at template level.
- **Resumption:** State lives entirely in markdown files on disk; resumption is "open the tasks.md and keep going."

### Amazon Kiro

- **What it is:** AWS's agentic IDE. Spec-driven with a hard three-file split.
- **URLs:** https://kiro.dev/docs/specs/ ; https://kiro.dev/docs/specs/feature-specs/ ; https://kiro.dev/docs/specs/best-practices/
- **Contract shape:** Three markdown files, per feature:
  - `requirements.md` — user stories plus acceptance criteria in **EARS** syntax. Direct quote of the pattern: *"WHEN [condition/event] THE SYSTEM SHALL [expected behavior]"*. Example: *"WHEN a user submits a form with invalid data THE SYSTEM SHALL display validation errors next to the relevant fields."*
  - `design.md` — "technical architecture, sequence diagrams, and implementation considerations."
  - `tasks.md` — checkbox-format tasks, each followed by implementation steps and a `_Requirements: 1.1, 1.5, 3.1, 3.2_` reference pointer back into `requirements.md`.
- **Proof-of-work:** "A task execution interface for `tasks.md` files that displays real-time status updates. Tasks are updated as in-progress or completed." The requirements-traceability string is the audit trail — each completed task cites the EARS rules it satisfied.
- **Terminal states:** Task-level — *in-progress* and *completed* are the only ones the docs surface.
- **Closeout/verification:** EARS acceptance criteria are the check; verification is implicit (task marked done = criteria met). No separate verifier agent in the public docs.
- **Scope controls:** `.kiro/steering/` markdown files provide persistent rules (workspace or global). Also accepts `AGENTS.md`. Agent hooks configured with `when`-type + pattern array trigger prompts on IDE events.
- **Retry:** Not documented publicly.
- **Resumption:** State is on-disk markdown; the three-file trio is the source of truth.

### Cursor (Plan Mode, Background Agents, Composer)

- **What it is:** Most-used AI editor. Cursor 3 split work into a "local plan → cloud background agent → pull back" model.
- **URLs:** https://cursor.com/blog/plan-mode ; https://cursor.com/docs/agent/plan-mode ; forum.cursor.com/t/background-agent-handoff/157041
- **Contract shape:** Plan Mode "creates a Markdown file with file paths and code references." Saved by default to home directory; users can opt to *"Save to workspace"* which puts it in `.cursor/plans/` for source control. No rigid schema — free Markdown, inline Mermaid supported.
  > *"Cursor researches your codebase to find relevant files, review docs, and ask clarifying questions… When you're happy with the plan, it creates a Markdown file with file paths and code references."*
- **Proof-of-work:** A new branch pushed to the repo (for background agents). No structured completion artifact — the PR diff *is* the artifact. Cursor 3 adds "seamless handoff" between local and cloud so sessions move rather than finalize.
- **Terminal states:** Not explicitly enumerated in public docs. Sessions end when the agent pushes or the user stops.
- **Closeout:** Agent "auto-runs all terminal commands, letting it iterate on tests" — closeout is in-band tool use, not a separate verifier role.
- **Scope controls:** Runs on its own branch in a cloned repo. Legacy `.cursorrules` + newer `.cursor/rules/*.mdc` define rules.
- **Retry:** In-band via the agent loop; no bounded harness-level retry disclosed.
- **Resumption:** "Move a local session to the cloud … pull a cloud session back to local" is an explicit, first-class primitive in Cursor 3. Session state is portable.
- **Important framing from Cursor's own docs:** *"reverting unsuccessful builds and refining the plan itself"* is the recommended iteration pattern — not retry, but *plan re-edit*.

### Claude Code (Plan Mode, Subagents, Skills)

- **What it is:** Anthropic's official CLI — ships to all Anthropic developers.
- **URLs:** https://code.claude.com/docs/en/sub-agents ; https://lucumr.pocoo.org/2025/12/17/what-is-plan-mode/
- **Contract shape:** Plan Mode produces a markdown `Plan.md`. Armin Ronacher, studying the system prompts directly: *"A plan in Claude Code is effectively a markdown file that is written into Claude's plans folder by Claude in plan mode… The generated plan doesn't have any extra structure beyond text."* He notes the system prompt is largely unchanged — plan mode is *"recurring prompts to remind the agent that it's in read-only mode."*
- **Subagent contract:** A markdown file in `.claude/agents/` (project) or `~/.claude/agents/` (personal) with YAML frontmatter (name, description, tools, model, permissionMode, etc.) + Markdown body for the system prompt. The parent Claude delegates based on the `description` field.
- **Proof-of-work:** *"the subagent … returns only a summary to the main thread."* Docs recommend the subagent produce a *durable artifact* (`research.md`, `plan.md`, `review-notes.md`) rather than just text. Content is unstructured markdown unless the subagent prompt enforces more.
- **Terminal states:** Not enumerated. Subagent returns a summary; failure is conveyed in text.
- **Closeout:** In-band. Not a separate harness step.
- **Scope controls:** YAML frontmatter restricts `tools`, `disallowedTools`, `permissionMode`. *"subagents cannot spawn other subagents"* — explicit depth limit of 1.
- **Retry / resumption:** Not formalized. Subagents are single-shot.

### Aider

- **What it is:** Widely-used open-source CLI coding assistant. Long-running; heavy benchmark culture.
- **URLs:** https://aider.chat/docs/more/edit-formats.html ; https://aider.chat/docs/usage/modes.html ; https://aider.chat/2024/09/26/architect.html
- **Contract shape:** *There is no contract artifact.* The "unit of work" is the **edit**, not the file. Aider ships five edit formats; the model emits edits inline in chat:
  - `whole` — "a full, updated copy of each source file that needs changes."
  - `diff` — "a series of search/replace blocks" (merge-conflict syntax).
  - `diff-fenced` — diff with filepath inside the fence (Gemini).
  - `udiff` — unified diff, line-numbers stripped because *"GPT is terrible at working with source code line numbers."*
  - `editor-diff / editor-whole` — for architect mode.
- **Proof-of-work:** Git commits. Aider auto-commits after each accepted edit. That *is* the audit trail. No separate JSON.
- **Terminal states:** Per-edit success/failure (the edit applies or doesn't). No run-level terminal state.
- **Closeout:** Architect mode separates reasoning from editing — *"An architect model will propose changes and an editor model will translate that proposal into specific file edits."* The handoff is **unstructured natural language** from architect to editor — explicitly *not* a schema: *"The Architect can describe the solution however comes naturally to it."*
- **Scope controls:** File-level — only files you've added to the chat via `/add` are editable. Everything else is read-only.
- **Retry:** Per-edit automatic repair attempts on malformed diffs; bounded; errors surface back to the model in-loop.
- **Resumption:** Git commits give natural resumption.

### Cognition Devin (sessions, playbooks)

- **What it is:** Early autonomous coding agent; shipped to enterprise customers.
- **URLs:** https://cognition.ai/blog/how-cognition-uses-devin-to-build-devin ; https://docs.devin.ai/product-guides/creating-playbooks ; https://docs.devin.ai/api-reference/sessions/retrieve-details-about-an-existing-session
- **Contract shape (Playbook):** Markdown, freeform, with named sections — *Overview, Procedure, Specifications, Advice, Forbidden Actions, Required from User*. Quote: *"Have one step per line, each line written imperatively … Mutually Exclusive and Collectively Exhaustive."* Success criteria go in *Specifications* as **postconditions**; guardrails in *Forbidden Actions*.
- **Contract shape (session):** A session has a prompt + source (GitHub repo), and can reference a `playbook_id`. Tasks via Slack, Jira, Linear, Web App, or REST API. For ticket systems: *"Devin analyzes the task, searches the codebase, and plans its approach. It generates a high-quality session prompt automatically."*
- **Proof-of-work:** Pull request + diff. Devin's API `Session` object fields include `status`, `messages`, `pull_request.url`, `structured_output` (customer-defined), `snapshot_id`.
- **Terminal states:** The session status enum is the richest we found in a shipping system. Documented values: `working`, `blocked`, `expired`, `finished`, `suspend_requested`, `suspend_requested_frontend`, `resume_requested`, `resume_requested_frontend`, `resumed`. Terminal: `finished`, `expired`. Suspend/resume are first-class.
- **Closeout:** *"If Devin Review or a GitHub bot flags bugs, Devin automatically fixes the PR. Devin also tackles CI/lint issues until all checks pass, closing the agent loop."* Verification is delegated to external CI signal.
- **Scope controls:** Playbook's *Forbidden Actions* section + sandboxed VM. Cognition's 2025 review explicitly says *"Devin excels at tasks with clear, upfront requirements and verifiable outcomes"* and *"Devin handles clear upfront scoping well, but not mid-task requirement changes."*
- **Retry:** Automatic loop closure against CI/PR review bot feedback. No stated bound.
- **Resumption:** First-class: `snapshot_id`, `suspend_requested → resume_requested → resumed` flow. `structured_output` is how the customer gets machine-readable results out.

### Google Antigravity

- **What it is:** Google's new agentic IDE; the explicit source of the "verifiable artifacts" framing in the user's brief.
- **URLs:** https://developers.googleblog.com/build-with-google-antigravity-our-new-agentic-development-platform/ ; https://www.index.dev/blog/google-antigravity-agentic-ide
- **Contract shape:** Task Lists + Implementation Plans. Users can *"leave feedback directly on the Artifact—similar to commenting on a doc."* Plan text is freeform; the system's emphasis is on human-readable deliverables.
- **Proof-of-work:** The strongest "verifiable artifact" story in the survey. Explicit typed outputs:
  - **Task Lists** — structured plan
  - **Implementation Plans** — architectural/technical detail
  - **Walkthroughs** — prose summary of changes + how to test
  - **Screenshots** — UI state before/after
  - **Browser Recordings** — agent's dynamic interactions
  - **Annotated code diffs**

  Direct quote: *"Delegating work to an agent requires trust, but scrolling through raw tool calls is tedious. Antigravity solves this by having agents generate Artifacts—tangible deliverables like task lists, implementation plans, screenshots, and browser recordings."*
- **Terminal states:** Not publicly schematized; execution ends when Artifacts are produced.
- **Closeout:** *The agent itself* verifies — *"starts the server and opens a browser to verify the app, performing manual testing like adding tasks and updating tasks."* Self-test via browser automation, then Walkthrough summarizes.
- **Scope controls:** IDE-enforced; not detailed publicly.
- **Retry / resumption:** Feedback on artifacts is *incorporated without stopping execution* — i.e., in-band correction rather than bounded retry.

### Factory AI (Droids, Droid Exec)

- **What it is:** Shipping CLI agent product. Headless `droid exec` is a reference point for production automation.
- **URLs:** https://factory.ai/news/code-droid-technical-report ; https://docs.factory.ai/cli/droid-exec/overview ; https://factory.ai/news/missions
- **Contract shape:** Prompt string, file path, or stdin. In spec mode (`--use-spec`) the agent "plans before executing." A separate `--spec-model` flag can route the planning phase to a different (typically stronger) model — explicit architect/executor split.
- **Proof-of-work:** Structured JSON output the cleanest we found at CLI level:
  ```
  {type, subtype, is_error, duration_ms, num_turns, result, session_id}
  ```
  `stream-json` mode provides a line-delimited JSONL of events (system init, messages, tool calls, results). Also `finalText`, `numTurns`, `durationMs`, `session_id`.
- **Terminal states:** Binary at the CLI layer — **exit 0 = success, non-zero = failure** (permission violation, tool error, unmet objective). *"Fail-fast behavior ensures no partial changes occur when actions exceed autonomy limits."*
- **Closeout:** Self-reports via JSON; no separate verifier in the CLI. Code Droid technical report: *"can choose to generate multiple trajectories for a given task, validate them using tests (existing and self-generated), and select optimal solutions."*
- **Scope controls:** Most detailed risk-tier system in the survey:
  - Default (read-only): file reads, git status, directory listing
  - `--auto low`: file edits in project dir
  - `--auto medium`: `npm install`, `git commit`, builds
  - `--auto high`: `git push`, arbitrary code execution, deploys
  - `--skip-permissions-unsafe`: all operations, sandbox only
  - `--cwd` caps working directory
  - `--enabled-tools / --disabled-tools` granular allow/deny
  - DroidShield does static analysis before commit
- **Retry:** Not disclosed as a harness primitive — multiple trajectories are model-driven.
- **Resumption:** `session_id` returned; JSON-RPC streaming protocol for multi-turn SDK integration.

### OpenHands (All-Hands-AI)

- **What it is:** Large open-source coding-agent platform; published SDK + paper.
- **URLs:** https://arxiv.org/html/2511.03690v1 ; https://github.com/All-Hands-AI/OpenHands
- **Contract shape:** No markdown-template contract. Task = a message to `send_message()` on a `Conversation`. Schema-strongest system in survey: *"agents are defined as stateless, immutable specifications"* serialized via Pydantic. `ToolDefinition` pairs an Action type and Observation type.
- **Proof-of-work:** An append-only **EventLog** of typed events. Actions (e.g. `CmdRunAction`, `FileEditAction`, `BrowseURLAction`, `IPythonRunCellAction`) + Observations (e.g. `CmdOutputObservation`) are Pydantic-validated. *"The system supports pause/resume at any point, with automatic state persistence."*
- **Terminal states:** `FINISHED`, `ERROR`, `PAUSED`, `WAITING_FOR_CONFIRMATION`. Explicit; richer than most.
- **Closeout:** *"Stuck detection mechanisms to identify infinite loops or redundant tool calls and intervene automatically"* — harness, not model, drives this.
- **Scope controls:** Security policies in the stateless agent spec; `WAITING_FOR_CONFIRMATION` state for high-risk actions.
- **Retry:** Not bounded at framework level; stuck-detection is the primary guard.
- **Resumption:** Event-sourced. *"State is managed through ConversationState, the only mutable component, which maintains an append-only EventLog recording all interactions. The system enables deterministic replay and recovery from incomplete conversations."*

### GitHub Copilot Workspace

- **What it is:** GitHub Next's shipping experiment. Most *formally staged* pipeline in the survey.
- **URLs:** https://github.com/githubnext/copilot-workspace-user-manual/blob/main/overview.md
- **Contract shape:** Two editable artifacts: **Topic** and **Plan**.
  - Topic: *"a question that can be posed against the codebase"* — one-sentence interrogative distilling the task.
  - Plan: list of files to *"edit, created, deleted, moved, or renamed"*, and per-file *"a list of specific steps that indicate the exact changes that need to be made."*
- **Proof-of-work:** CLI session artifacts stored in `~/.copilot/session-state/{session-id}/` including `plan.md`. Generated file contents produced "one-by-one"; user reviews inline and can selectively regenerate ("checkboxes + Update selected files").
- **Terminal states:** Not explicitly enumerated.
- **Closeout:** Integrated Codespace terminal lets the user run builds, lint, tests — *"a secure sandbox with a full development environment installed."* Verification is human-in-loop via terminal.
- **Scope controls:** The Plan's explicit file list is the scope. *"View references"* button exposes scope.
- **Retry:** Per-file regeneration via plan edit; no bounded automatic retry.

### Block Goose (recipes, allowlist)

- **What it is:** Block's open-source MCP-native agent. Recipe format is YAML (distinctive).
- **URLs:** https://block.github.io/goose/docs/guides/recipes/recipe-reference/ ; https://github.com/block/goose/blob/main/crates/goose-server/ALLOWLIST.md
- **Contract shape:** YAML recipe with fields for `parameters` (typed: string/number/select/file; `required`/`optional`), workflow steps, optional JSON-schema `response` block for structured output, and `sub_recipes` array for delegation. This is the *most schematic* contract artifact in the survey.
- **Proof-of-work:** Customer-defined — recipes can declare a JSON schema for output; runtime validates.
- **Terminal states:** Recipe completion is success/fail; sub-recipes run in isolation.
- **Closeout:** Customer-defined via response schema.
- **Scope controls:** External YAML **allowlist** fetched from `GOOSE_ALLOWLIST` URL; the allowlist lists allowed MCP extensions (`id` + `command` fields). *"By default, goose will let you run any MCP via any command, which isn't always desired."*
- **Retry / resumption:** Not prominently documented.

### Sweep AI

- **What it is:** GitHub-issue-driven PR bot; shipped, open-source.
- **URLs:** https://github.com/sweepai/sweep/blob/main/sweep.yaml ; https://docs.sweep.dev/
- **Contract shape:** **A GitHub issue**, labeled `sweep` or with title starting `Sweep:`. After triggering, *"Sweep will post a comment acknowledging the task and outlining its intended plan, and you can reply to this comment to adjust the plan before Sweep begins coding."*
- **Proof-of-work:** A PR. Sandbox outputs (lint/test results) attached during the run.
- **Terminal states:** PR opened (success) or comment with failure reason.
- **Closeout:** Sandbox runs static analysis, linters, type-checkers, tests after every file edit: *"checks against static code analysis tools like formatters, linters, type-checkers and tests after every file edit, ensuring pristine generated code."* In-band, harness-driven.
- **Scope controls:** `sweep.yaml` specifies:
  ```yaml
  branch: "main"
  blocked_dirs: ["..."]
  draft: False
  rules: [...]
  ```
  `blocked_dirs` is a **blacklist**, not a whitelist. Rules are imperative coding standards ("Use loguru for error logging", "No print statements in production", "Type hints on all parameters").
- **Retry:** Re-triggered via GitHub comment. Sandbox loop iterates until tests pass.

### Replit Agent

- **What it is:** Shipping agentic builder with distinctive billing model.
- **URLs:** https://docs.replit.com/replitai/agent ; https://blog.replit.com/effort-based-pricing
- **Contract shape:** Prompt → plan → execute → iterate loop. Plan is freeform natural language; displayed in Kanban-style task system.
- **Proof-of-work:** **Checkpoints** — snapshots of project state at each milestone. Billing is tied to checkpoints. Distinctive because POW = revertible state, not artifact description.
- **Terminal states:** Not publicly schematized.
- **Closeout:** Agent runs its own server, tests in workspace; checkpoint persists when milestone completes.
- **Scope controls:** Project-sandboxed.
- **Retry:** Rollback-to-checkpoint is the primary "retry" primitive — not re-run but revert and re-prompt.

### Windsurf Cascade

- **What it is:** Codeium's IDE agent. Has dedicated Plan Mode.
- **URLs:** https://docs.windsurf.com/windsurf/cascade/memories
- **Contract shape:** Plans are structured markdown with *"implementation phases"*; memories store rules separately. Plan Mode (rolled out late 2025): *"Plan mode helps AI work on more complex tasks. What usually happens when you give a longer task, like refactor, AI tends to drift away from the goal, and loose context over time. Plan mode helps with that."*
- **Proof-of-work:** Not a distinct artifact — IDE diff is the output.
- **Scope controls:** Rules via `trigger:` field — `always_on`, `model_decision`, `glob`, `manual`. Character caps: global rules 6,000 chars; workspace 12,000/file.
- **Resumption:** Memories stored at `~/.codeium/windsurf/memories/`; auto-generated per workspace. *"Auto-generated memories live only on your machine. If you want Cascade to remember something durably — and share it with your team — ask Cascade to write it to a Rule."*

### Google Jules

- **What it is:** Google's async coding agent with a documented REST API. Schema is explicit.
- **URLs:** https://developers.google.com/jules/api ; https://jules.google
- **Contract shape:** `Session` (prompt + `Source` = GitHub repo). Core types: `Session`, `Activity`, `Source`. Activity `originator` ∈ {"user", "agent"}.
- **Proof-of-work:** Activities carry `artifacts`:
  - `changeSet` — git patches with base commit ID + suggested commit message
  - `bashOutput` — output + exit code
  - `media` — PNG screenshots
  Plans appear as `planGenerated` activity; user `planApproved` (auto-approved via API by default). Completion = `sessionCompleted: {}` activity.
- **Terminal states:** Activity-event-driven. `sessionCompleted` is the explicit terminal marker.
- **Resumption:** Activity log is the durable state.

### OpenAI Codex CLI

- **What it is:** OpenAI's shipping CLI coding agent (Rust core, ~95%
  codex-rs). Uses the Responses API. Distinct from the 2021 Codex model.
- **URLs:** https://developers.openai.com/codex/ ; https://openai.com/index/unrolling-the-codex-agent-loop/ ; https://cookbook.openai.com/examples/gpt-5/codex_prompting_guide ; https://developers.openai.com/codex/cli/features ; https://developers.openai.com/codex/subagents ; https://developers.openai.com/api/docs/guides/tools-apply-patch ; https://developers.openai.com/codex/guides/agents-md ; https://agents.md/
- **Contract shape (per task):** Just the prompt. No contract file. Plan
  is not a separate artifact — planning is a tool call (`update_plan`)
  inside the main agent loop. Prompting guide instructs: *"1 sentence
  acknowledgement, 1-2 sentence plan"* then tool use, with plan status
  mutated in-line.
- **Contract shape (ambient, per-repo):** `AGENTS.md` — *"AGENTS.md is
  just standard Markdown. Use any headings you like."* Explicitly *"no
  frontmatter (unless you want it), no specific sections, no special
  syntax."* Precedence: `~/.codex/AGENTS.override.md` or
  `~/.codex/AGENTS.md`, then walk project root → cwd reading each
  directory's `AGENTS.md`. *Stewarded by the Agentic AI Foundation under
  the Linux Foundation*; compatible with Codex, Copilot, Cursor, Jules,
  Aider, Factory, Amp. This is ambient context, **not** a per-task
  contract.
- **Architecture:** Single-agent loop. *Not* CIV. *Not* architect/editor
  split at the model level. Planning, execution, and in-band
  verification all run inside one agent against the Responses API.
  `agents.max_depth=1` for subagents (no recursion).
- **Proof-of-work:** Git diff / `apply_patch` hunks. No separate
  structured completion artifact — the patch is the output.
- **Terminal states:** Not enumerated as a state machine. Agent runs
  until done, blocked, or out of budget. Prompting guide: *"do not end
  your turn with clarifications unless truly blocked."* Blocking is
  discouraged, not first-classed.
- **Closeout / verification:** In-band by default — agent runs tests,
  linters, pre-commit checks as ordinary shell tool calls. *"Codex
  produces higher-quality outputs when it can verify its work,
  including steps to reproduce an issue, validate a feature, and run
  linting and pre-commit checks."* A separate **reviewer** is opt-in,
  user-triggered: *"The CLI launches a dedicated reviewer that reads
  the diff you select and reports prioritized, actionable findings
  without touching your working tree. By default it uses the current
  session model; set `review_model` in config.toml to override."* Key
  properties: diff-only input, read-only tools, same model by default,
  findings cite specifics.
- **Tool architecture:** Function-calling via Responses API; MCP
  supported for third-party tools. **Mutations go through `apply_patch`
  only** — unified-diff-with-envelope tool. OpenAI: *"the model has
  been trained to excel at this diff format."* Reads use shell-first
  (`cat`, `rg`, `grep`, `find`).
- **Subagents:** Opt-in, parallel fan-out only. *"Codex only spawns
  subagents when you explicitly ask it to."* `agents.max_depth`
  defaults to 1. Used for parallel codebase exploration or independent
  subtasks. Not a CIV pipeline.
- **History / context:** Server-side compaction via the
  `/responses/compact` endpoint, which returns an `encrypted_content`
  item preserving latent state. *"Automatic compaction pauses after 3
  consecutive failures."* Tighter coupling to the inference API than
  most harnesses.
- **Retry / blocked:** No explicit state machine. Norm ("don't end with
  clarifications unless truly blocked"), not mechanism. The one
  built-in pause point is approvals for out-of-sandbox commands; a
  rejected approval feeds back to the agent to adjust.

---

## MEDIUM-WEIGHT SOURCES

### SWE-agent (Princeton/Stanford)

- **What it is:** The seminal academic paper on Agent-Computer Interfaces; NeurIPS 2024.
- **URLs:** https://arxiv.org/abs/2405.15793 ; https://github.com/princeton-nlp/SWE-agent
- **Contract shape:** Task = a GitHub issue body. No separate contract markdown.
- **Proof-of-work:** A "trajectory" — *"full history log of the run"* with LM thoughts/actions (shown blue in the paper figures) and computer feedback (red). Versions after the paper replaced the `messages` field with `query`. Submission is a patch.
- **Terminal states:** `submit` command ends the trajectory.
- **Closeout:** External — SWE-bench test suite is run post-hoc.
- **Scope controls:** Config is a single YAML per agent. The ACI *"specifies the commands available to the LM and how the environment state after the execution of each command will be communicated back to the LM."* Design principle: *"Actions should be simple and easy to understand … efficient, with important operations consolidating into as few actions as possible."* Default config forbids modifying test files.
- **Why medium-weight:** Strong academic influence, production analog is OpenHands.

### Augment Code — Coordinator/Implementor/Verifier

- **What it is:** Augment's named writeup of the three-role pattern, plus their "Intent" product implementing it.
- **URLs:** https://www.augmentcode.com/guides/coordinator-implementor-verifier
- **Direct quotes:** Coordinator *"Decomposes spec into DAG [and] Computes topological order."* Implementor *"runs a ReAct-style reason-act-observe cycle in its own isolated context."* Verifier *"Validates against original spec."*
- **Structured data contracts:** *"structured contracts ensure only declared variables pass forward. The Coordinator then merges their structured outputs into a single dictionary"* — explicit "pull model" for context.
- **Retry semantics (the most specific in the survey):** *"Default configuration: 3 attempts per subtask, with a separate 5-iteration cap on replanning. On retry-budget exhaustion, escalation should trigger replanning or human routing; indefinite retry loops waste tokens without improving outcomes."*
- **Human gates:** Reviewers approve the Coordinator's DAG before execution and review Verifier output before merge.
- **Why medium-weight:** It's a vendor guide (semi-marketing) but with concrete numbers and a shipping implementation.

### OpenHands SDK paper (arXiv 2511.03690)

Covered above under OpenHands — paper is the formal description of the shipping platform.

---

## WHERE THE SHIPPING SYSTEMS DISAGREE

### On contract format
- **Prose markdown, no schema:** Claude Code Plan Mode (*"no extra structure beyond text"*), Aider (no contract at all — the edit is the unit), Cursor Plan Mode (*"a Markdown file with file paths and code references"*), AGENTS.md (*"no specific sections"*), Antigravity Implementation Plans (free-comment-on-a-doc).
- **Structured markdown with strict per-line syntax:** Spec Kit (`- [ ] T001 [P] [US1] Description with file path`), Kiro tasks.md (`- [ ] 1. description` + `_Requirements: 1.1, 1.5_`).
- **Three-file markdown ensemble:** Kiro (requirements.md + design.md + tasks.md) and Spec Kit (constitution.md + spec.md + plan.md + tasks.md + optional research/data-model/contracts/).
- **Named markdown sections:** Devin Playbooks (Overview, Procedure, Specifications, Advice, Forbidden Actions, Required from User).
- **YAML + schema:** Block Goose recipes (typed parameters, JSON-schema response), OpenHands (Pydantic-validated ToolDefinition/Action/Observation).
- **Topic + Plan two-stage:** Copilot Workspace uses an explicit interrogative Topic first, then a file-step Plan.
- **Prompt + repo only:** Devin session, Jules session, Cursor background agent, Sweep (GitHub issue), Replit. No explicit contract file.
- **Bolt.new:** HTML-tag-wrapped execution plan — `<boltArtifact>` with `<boltAction>` elements. Singular for the project.

### On proof-of-work / completion artifact
- **Pull request + diff only:** Sweep, Jules, Cursor Background, Devin.
- **Git commits inline:** Aider.
- **Checkbox flipped:** Spec Kit, Kiro.
- **Structured JSON + session ID:** Factory Droid Exec (`{type, subtype, is_error, duration_ms, num_turns, result, session_id}`), Jules (`changeSet`, `bashOutput`, `media`).
- **Append-only event log:** OpenHands EventLog, SWE-agent trajectory.
- **Rich "verifiable artifacts" bundle:** Antigravity — task list + implementation plan + walkthrough + screenshots + browser recordings + annotated diff.
- **Revertible state snapshot:** Replit Checkpoints — billing-tied, rollback-first.
- **Agent writes a summary markdown file:** Claude Code subagents (recommended, not enforced).

### On terminal states (the biggest disagreement)
- **Not enumerated:** Cursor, Aider, Claude Code, Kiro, Spec Kit, Replit, Cursor, Antigravity.
- **Binary exit code:** Factory Droid (exit 0 / non-zero).
- **Activity-event sentinel:** Jules (`sessionCompleted: {}`).
- **Small explicit enum:** OpenHands (`FINISHED`, `ERROR`, `PAUSED`, `WAITING_FOR_CONFIRMATION`).
- **Large explicit enum with suspend/resume:** Devin (`working`, `blocked`, `expired`, `finished`, `suspend_requested`, `suspend_requested_frontend`, `resume_requested`, `resume_requested_frontend`, `resumed`).
- **"Blocked" as a first-class state:** Only Devin among shipping systems. Copilot Workspace and Antigravity implicitly handle it by keeping the human in loop. Everyone else either fails silently or retries.

### On scope / trust controls
- **Whitelist (explicit files in chat):** Aider `/add`.
- **Blacklist:** Sweep `blocked_dirs`.
- **Risk tiers:** Factory Droid (`--auto low/medium/high`) — the most granular.
- **Allowlist by MCP extension:** Goose `GOOSE_ALLOWLIST`.
- **Per-tool enable/disable:** Claude Code subagents (`tools`, `disallowedTools`), Factory (`--enabled-tools`/`--disabled-tools`).
- **Branch isolation:** Cursor Background Agents, Devin (VM), Factory (sandbox).
- **File-list-in-task:** Spec Kit, Kiro, Copilot Workspace.
- **Rules with activation triggers:** Windsurf (`always_on`/`model_decision`/`glob`/`manual`).
- **Model-level static analysis gate:** Factory DroidShield.
- **In-spec "Forbidden Actions":** Devin Playbooks.

### On closeout / verification
- **In-band tool use (no separate verifier):** Cursor, Aider, Claude Code, Replit, Kiro.
- **External CI signal:** Devin (PR bot / CI loop).
- **Harness-driven sandbox after every edit:** Sweep (lint + typecheck + test).
- **Agent self-tests via browser:** Antigravity (launches server, tests in browser, writes walkthrough).
- **Architect/editor (planning vs application, not verification):** Aider, Factory `--spec-model`, Cursor Plan vs Build, Copilot Workspace Topic vs Plan vs Implementation.
- **Coordinator/Verifier (dedicated verifier role):** Augment Intent, VeriMAP.
- **Stuck detection:** OpenHands.
- **Explicit acceptance-criteria pointers:** Kiro (`_Requirements: 1.1, 1.5_` per task).

### On retry semantics
- **Unbounded model-driven loop:** Most IDEs.
- **Bounded with explicit numbers:** Augment CIV (*3 attempts per subtask, 5 replans*).
- **Revert-and-refine the plan:** Cursor's own recommendation (*"reverting unsuccessful builds and refining the plan itself … is often faster than fixing an in-progress agent"*), Replit checkpoint rollback.
- **CI-signal retries:** Devin.
- **Fail-fast:** Factory (*"Fail-fast behavior ensures no partial changes occur when actions exceed autonomy limits."*).

### On resumption
- **First-class move-state-between-environments:** Cursor 3 (local ↔ cloud).
- **Suspend/resume in API:** Devin, OpenHands.
- **Snapshot/rollback:** Replit Checkpoints.
- **Event-sourced replay:** OpenHands, Jules activity log.
- **State in on-disk markdown:** Spec Kit, Kiro, Cursor Plan Mode, Claude Code Plan Mode, Windsurf.
- **Nothing explicit:** Aider (relies on git), Sweep (retrigger by comment).

---

## NOTABLE ABSENCES / SURPRISES

- **Kiro's internal tasks.md template isn't fully public.** The docs describe the three-file structure and show the checkbox format (`- [ ] 1. description` with `_Requirements: …_`) but the canonical in-IDE template isn't published like Spec Kit's is. Third-party docs (kiro.directory, AWS blogs) fill in the gap but aren't primary.
- **Antigravity's artifact schemas aren't public either.** Google's marketing describes *Task Lists, Implementation Plans, Walkthroughs, Screenshots, Browser Recordings* as categories but no JSON/markdown schema exists on the public docs site at time of research — only the conceptual framing. The `antigravity.google/docs/artifacts` and `/implementation-plan` URLs returned empty content via WebFetch.
- **"Blocked" is nearly absent as a first-class terminal state.** Devin is the only shipping product that exposes it in its documented session enum. Everyone else folds it into either "paused," "waiting for confirmation," or silent failure. The user's BRIEF has `blocked` as a core terminal state — this is a minority position among shipping systems, notably.
- **Dedicated Verifier sub-agents are rare in shipping.** Sweep's sandbox, Antigravity's browser self-test, and OpenHands' stuck-detector all count, but a *named separate agent* that validates acceptance is mainly an Augment/VeriMAP story — not yet the norm. Claude Code subagents *can* do this but it's convention, not harness-enforced.
- **Spec Kit's documented failure mode is directly relevant.** From GitHub discussion #1619: *"How do you prevent 'TODO = completed task' behavior? Many tasks were marked 'done' with only `// TODO` comments."* And *"Spec Kit doesn't guarantee and validate that implementation actually follows the task plan."* And Scott Logic's Eberhardt: *"33.5 minutes of agent execution and 2,577 lines of markdown to produce 689 lines of functional code—roughly 3.7x slower than his standard iterative approach."* — *"Specifications … lack this formality. They are not a law I would put my trust in."* A checkbox that the model itself flips is not proof-of-work.
- **AGENTS.md is the one convergence.** The *only* artifact format in wide cross-vendor use is `AGENTS.md`, and it is explicitly *not* a task contract — it's ambient context. This is worth noting because the absence of cross-vendor convergence on *task* artifact is striking.
- **"Edits are the unit of work" vs "files are the unit" vs "tasks are the unit":** Aider pushes edits; Copilot Workspace pushes file-level plans; Spec Kit/Kiro push task-level plans. These are different atomicity choices and they imply different retry and verification strategies. No one has resolved this.
- **Sweep's scope control is a blacklist (`blocked_dirs`), not a whitelist.** Contra your BRIEF's "explicit file list" (which aligns with Spec Kit, Kiro, Copilot Workspace). In practice nobody shipping a product uses a per-task whitelist for scope the way your design proposes — closest is Spec Kit/Kiro listing files in-task, but without harness enforcement that changes outside that list fail.
- **Architect/editor split appears more than named planner/verifier:** Aider architect+editor, Factory `--spec-model`, Cursor Plan+Build, Copilot Workspace Topic+Plan+Implementation. This is much more common than Coordinator/Implementor/Verifier. The CIV pattern in your BRIEF is more aligned with Augment and VeriMAP than with the majority of shipping IDEs.
- **Paywall / marketing-only sources:** Lovable's documentation is mostly prompting guidance; they don't publish an artifact schema. Bolt.new's artifact format (`<boltArtifact>` / `<boltAction>`) is visible only via leaked system prompts and is a *runtime* format (streamed into an in-browser VM), not a user-visible contract. Replit's task-system internals (Kanban structure, checkpoint format) are not publicly schematized — only the conceptual model. Windsurf Plan Mode's on-disk plan format is not publicly documented in the memories page we could access.

---

## Sources (URLs for verification / deeper dives)

- [Kiro specs](https://kiro.dev/docs/specs/) / [Feature specs](https://kiro.dev/docs/specs/feature-specs/) / [Steering](https://kiro.dev/docs/cli/steering/)
- [Kiro EARS format guide](https://kiro.directory/tips/ears-format)
- [Spec Kit tasks-template.md](https://github.com/github/spec-kit/blob/main/templates/tasks-template.md) / [plan-template.md](https://github.com/github/spec-kit/blob/main/templates/plan-template.md) / [tasks command](https://github.com/github/spec-kit/blob/main/templates/commands/tasks.md)
- [Spec Kit + Copilot failure discussion #1619](https://github.com/github/spec-kit/discussions/1619)
- [Scott Logic: Putting Spec Kit Through Its Paces](https://blog.scottlogic.com/2025/11/26/putting-spec-kit-through-its-paces-radical-idea-or-reinvented-waterfall.html)
- [Aider edit formats](https://aider.chat/docs/more/edit-formats.html) / [Chat modes](https://aider.chat/docs/usage/modes.html) / [Architect/editor post](https://aider.chat/2024/09/26/architect.html)
- [Cursor Plan Mode blog](https://cursor.com/blog/plan-mode) / [Plan Mode docs](https://cursor.com/docs/agent/plan-mode)
- [Cursor Background Agents forum](https://forum.cursor.com/t/background-agent-handoff/157041)
- [Claude Code subagents](https://code.claude.com/docs/en/sub-agents)
- [Armin Ronacher: What Actually Is Claude Code's Plan Mode?](https://lucumr.pocoo.org/2025/12/17/what-is-plan-mode/)
- [Devin Playbooks](https://docs.devin.ai/product-guides/creating-playbooks) / [Session API schema](https://docs.devin.ai/api-reference/sessions/retrieve-details-about-an-existing-session) / [2025 performance review](https://cognition.ai/blog/devin-annual-performance-review-2025) / [How Cognition uses Devin to build Devin](https://cognition.ai/blog/how-cognition-uses-devin-to-build-devin)
- [Factory Code Droid technical report](https://factory.ai/news/code-droid-technical-report) / [Droid Exec docs](https://docs.factory.ai/cli/droid-exec/overview)
- [OpenHands SDK paper (arXiv 2511.03690)](https://arxiv.org/html/2511.03690v1)
- [Copilot Workspace user manual overview](https://github.com/githubnext/copilot-workspace-user-manual/blob/main/overview.md)
- [Block Goose recipe reference](https://block.github.io/goose/docs/guides/recipes/recipe-reference/) / [ALLOWLIST.md](https://github.com/block/goose/blob/main/crates/goose-server/ALLOWLIST.md)
- [Sweep sweep.yaml](https://github.com/sweepai/sweep/blob/main/sweep.yaml)
- [Replit Agent docs](https://docs.replit.com/replitai/agent) / [Effort-based pricing](https://blog.replit.com/effort-based-pricing)
- [Windsurf Cascade memories](https://docs.windsurf.com/windsurf/cascade/memories)
- [Jules API docs](https://developers.google.com/jules/api)
- [Antigravity developers blog launch](https://developers.googleblog.com/build-with-google-antigravity-our-new-agentic-development-platform/)
- [SWE-agent paper (arXiv 2405.15793)](https://arxiv.org/abs/2405.15793) / [GitHub](https://github.com/princeton-nlp/SWE-agent)
- [Augment CIV guide](https://www.augmentcode.com/guides/coordinator-implementor-verifier)
- [AGENTS.md spec](https://agents.md/) / [OpenAI Codex AGENTS.md guide](https://developers.openai.com/codex/guides/agents-md)
