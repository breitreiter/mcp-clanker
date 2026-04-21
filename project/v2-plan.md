# Plan to reach v2

## Premise

The limiting factor is one operator's ability to review, understand,
and validate the system. Not code velocity.

Each phase ships something legible — a trace you can read, a diff you
can eyeball, a terminal state you can interpret. Between phases you
actually use the system on real work and form opinions. If a phase's
output surprises you, we adjust before moving on.

"v2" here means: **you trust this system enough to use it at work.**
That bar requires observability, safety, a real verification layer,
and a sandbox. Everything else is deferred.

## The path

| Phase | Goal | Ships | How you validate | Sessions |
|---|---|---|---|---|
| **1. Instruments** | Read a run and know what happened, unaided | Execution trace (JSONL), rendered transcript (md), scope-adherence flag in POW, token + cost in POW | Run T-001, open the trace, reconstruct turn-by-turn without asking | 2 |
| **2a. Skill** | Claude Code knows how to use the system without pre-arranged briefing | Minimal skill covering: when to delegate, how to use the contract template, proof-of-work interpretation, `blocked_question.category` retry loop, cost framing. Flip `build`'s MCP description from ALPHA-warning to a working one | Driving a build through Claude Code (not `--build` CLI) gets a sensible attempt without me hand-holding the parent | 0.5 |
| **2. Real contract** | Collect actual failure data, not theoretical | Prompt externalized + Codex-aligned (`Prompts/AzureFoundry.md`); pick 1–2 medium contracts and run them through Claude Code | Read each run's trace + diff + POW and describe in your own words what it did well / poorly | 1 |
| **3. Safety + toolkit** | System stops driving off cliffs; has the tools rote work actually needs | Safety gates (`CommandClassifier`, network-egress, doom-loop), `apply_patch`, `grep` / `list_dir` / `todo_*`, file tool polish, remaining MCP handlers stop being stubs | Adversarial contract: off-scope bash command blocks with the right `blocked_question.category` | 2–3 |
| **4. Self-check** | First honest "is it correct" signal beyond the model's own word | Terminal-turn acceptance self-check: `{item, pass/fail, citation}` per Acceptance bullet | Run a contract where you deliberately leave some acceptance items unmet; check the self-check is honest about it | 1 |
| **5. Closeout** | Independent verification — the real quality gate | Diff-only reviewer sub-agent per Codex pattern; POW's `acceptance[]` populated by closeout, not executor self-report | "Lying" contract (executor claims success but didn't do the work) gets caught by the reviewer | 2 |
| **6. Docker** | Work-use gate. The v2 line | `docker run` with `--network=none` (or restricted bridge), worktree bind mount, resource limits; minimal image with dotnet SDK + git + UNIX basics | Exfil-attempt contract fails because there's no network, not because our checks fired | 1–2 |

**~9–11 sessions total.** At "work on this when you feel like it"
cadence, probably 3–4 weeks of calendar time. Each session ends with
something demoable.

## Why phase 1 before anything else

Without observability you can't form opinions about what's broken,
and without opinions the rest of the plan is guessing. The instruments
are load-bearing — every subsequent decision is easier to make with a
trace in front of you. Resist the temptation to skip ahead.

## Explicitly deferred past v2

Not on the v2 path unless we hit real signal that we need them:

- **Spawn tool / sub-agents.** Fan-out exploration, not pipeline.
  Codex validates the shape. Add when a contract genuinely wants
  parallelism.
- **Repo map generator.** Static analysis + cached one-liners.
  Useful later; irrelevant until we have contracts that would benefit
  from repo-wide orientation.
- **Session resume.** Needs at least one real failure mode to design
  against. Worktree + branch already gives us a passable manual resume.
- **Retry semantics.** Augment's 3-attempt / 5-replan numbers are the
  starting point. Only relevant once v2 closeout can bounce work back
  in-loop.
- **External MCP client support.** Low demand in our specific use case
  (user doesn't heavily use MCP tools in practice). Keep tool-assembly
  open to external concatenation so we're not boxed in; pick up when
  a real contract would benefit.
- **History compaction.** May be free via Azure Foundry's Responses
  API `/responses/compact` endpoint. Check availability then; don't
  port nb's manual compaction preemptively.
- **Per-provider prompt variants** (beyond `AzureFoundry.md`).
  Author when we actually run contracts on Anthropic / OpenAI / Gemini
  and see quality degrade, not before.
- **Multi-provider write-surface strategy** (`apply_patch` vs
  `edit_file` split). Research + test once we care about Anthropic
  quality; see TODO.md #6.

## Two honesty checks

1. **After every phase, you use the system on something real.** No
   building three phases deep without running contracts. Opinions from
   live runs beat opinions from design docs.
2. **The plan bends to data.** If phase 2's runs reveal that the
   biggest pain is something we didn't anticipate (e.g., history
   bloat, prompt drift, a specific tool's failure mode), we re-order.
   The phase structure is a scaffold, not a contract.

## See also

- `TODO.md` — item-level detail for everything ships in the phases
  above, plus hygiene and measurement work
- `executor-v1-research.md` — design decisions that shaped v1
- `bucket-a-survey.md` — primary-source scan of shipping hand-off
  patterns (the research that informed "architect/editor not CIV",
  the reviewer pattern, etc.)
- `BRIEF.md` — the original framing document
