# imp

C# stdio MCP server that hands structured coding tasks (*contracts*) from Claude Code to a cheaper executor model. Intended for rote, well-scoped work you don't want to burn Opus tokens on.

## Status

**Beta.** v2-plan phases 1–6 have all shipped and been validated end-to-end. The executor runs medium contracts with:

- **Safety gates** — danger-pattern classifier (blocks `rm -rf`, `sudo`, forkbombs, etc.), network-egress gate (blocks `curl` / `wget` / `ssh` / `gh api` and mutating `gh` subcommands, with a localhost exemption), doom-loop detector (3× same tool args or 5 consecutive failures).
- **Closeout verification** — fresh-context reviewer with read-only tools independently verifies every Acceptance bullet against the actual diff. Overrides the executor's self-report; demotes `success` → `failure` when any bullet flunks, with a citation saying why.
- **Full toolkit** — `bash`, `read_file`, `write_file`, `apply_patch` (codex sentinel format, preferred for GPT-family models), `grep`, `list_dir`, `todo_read`, `todo_write`.
- **Docker sandbox** — opt-in per-command containerization with `--network=none` and a shared nuget cache volume. Packages already downloaded work offline; genuinely-new packages fail to restore so the parent owns package-adoption decisions, not the executor.

Remaining gaps before GA: a `**Allowed network:**` contract declaration (so contracts that genuinely need network can opt in), config-driven `MaxOutputTokens` per provider, Windows shell detection, MCP-server hot-reload shim for faster iteration. See `project/TODO.md`.

## What it does

1. Claude Code (Opus-scale planning) writes a **contract** — a markdown file describing a focused change: goal, explicit file scope, contract body, acceptance criteria, non-goals. Template at `Templates/contract.md` and exposed as the MCP resource `template://contract`.
2. `imp`'s `build` tool parses the contract, creates a fresh git **worktree** on a new branch (`contract/T-NNN`) in the target repo, and runs an **executor** — a tool-calling loop against the configured provider (primary target: Azure Foundry / gpt-5.1-codex-mini).
3. Safety gates pre-flight every bash command and watch the tool-call history for loops. Any breach terminates the run with a structured `blocked_question`.
4. After the executor terminates cleanly, a one-turn **self-check** asks the model to report pass/fail per acceptance bullet with a citation, then a fresh-context **closeout reviewer** independently verifies each bullet against the actual worktree diff.
5. Returns a **proof-of-work** JSON: terminal state, tool-call count, token usage, estimated cost, files changed, scope-adherence report, closeout verdicts with citations, worktree + branch + trace paths. A full execution **trace** (JSONL) and a rendered **transcript** (md) land in a sidecar directory for forensic reading.

## Why

Plan with Opus (expensive, scarce). Grind with a cheap model (Azure GPT-5.1-codex-mini at ~$0.05–0.15 per non-trivial contract). Keep human review cost bounded by a structured, scannable proof-of-work. The parent never has to read the full transcript of the child's work — only the shape of what was attempted, whether independent verification agreed, and where to look deeper when it didn't.

## Providers

Configured via `appsettings.json`. See `appsettings.example.json` for shape.

- **AzureFoundry** — Azure's OpenAI Responses API. Primary target.
- **AzureOpenAI** — Chat Completions.
- **OpenAI**
- **Anthropic**
- **Gemini**

## Prerequisites

- .NET 8 SDK
- `git`
- Linux or macOS. Windows support is planned but not wired — see `project/TODO.md`.
- Docker (optional — only if you want the sandbox isolation described below; host mode works without it)
- Credentials for at least one supported provider

## Setup

```bash
git clone https://github.com/breitreiter/imp.git
cd imp
cp appsettings.example.json appsettings.json
# edit appsettings.json and fill in provider credentials
dotnet build
```

`appsettings.json` is gitignored.

## Running

### Smoke-test provider wiring

```bash
dotnet run -- --ping                  # uses ActiveProvider from config
dotnet run -- --ping AzureFoundry     # override
```

### Exercise the build loop from the CLI

```bash
dotnet run -- --build path/to/contract.md
dotnet run -- --build path/to/contract.md AzureFoundry
```

The build command creates a worktree at `<parent>/<repo>.worktrees/<task-id>/` and a trace sidecar at `<parent>/<repo>.worktrees/<task-id>.trace/` (JSONL + rendered transcript). Neither is cleaned up automatically.

### Regenerate a transcript from a trace

```bash
dotnet run -- --render-transcript <parent>/<repo>.worktrees/<task-id>.trace/trace.jsonl
```

Writes `transcript.md` next to the input. Useful for older traces written before the renderer shipped, or for iterating on the renderer itself.

### Via Claude Code MCP

Register the server in your Claude Code MCP config:

```json
{
  "mcpServers": {
    "imp": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/imp"]
    }
  }
}
```

The server exposes:

| Tool | What |
|---|---|
| `ping` | Smoke-test the configured provider |
| `build(contractPath, [targetRepo])` | Run a contract end-to-end; returns proof-of-work JSON |
| `validate_contract(contractPath, [targetRepo])` | Parse + validate without executing |
| `list_tasks([targetRepo])` | List contracts under `<target>/contracts/` |
| `get_contract(taskId, [targetRepo])` | Return a contract's raw markdown by task ID |
| `get_log(taskId, [targetRepo])` | Return the rendered transcript of a task's most recent run |
| `update_contract(contractPath, content)` | Write a contract file |

Plus two MCP resources: `template://contract` (skeleton for a new contract) and `template://proof-of-work` (example of what `build` returns).

### Install the Claude Code skill

The `skills/imp.md` file in this repo teaches Claude Code when to delegate, how to write contracts, how to interpret proof-of-work, and the `blocked_question.category` retry loop. Install it once at user scope so it's active in any repo where the MCP server is registered:

```bash
mkdir -p ~/.claude/skills
ln -s "$(pwd)/skills/imp.md" ~/.claude/skills/imp.md
# or copy instead of symlink if you'd rather not track repo edits:
#   cp skills/imp.md ~/.claude/skills/imp.md
```

Restart the Claude Code session after installing so the skill is picked up.

### Build the Docker sandbox (optional, recommended for real use)

Imp defaults to `Sandbox.Mode=Host`, which runs `bash` directly on the host — convenient during development, but no filesystem or network isolation beyond the application-layer safety gates. For production-like use, build the sandbox image:

```bash
./sandbox/build.sh
```

That builds `imp-sandbox:latest` (dotnet SDK 8 + git) and creates the `imp-nuget` Docker volume that shares the package cache across contracts. Then flip `Sandbox.Mode` to `"Docker"` in `appsettings.json`.

In Docker mode, each `bash` call runs in a throwaway container with:

- `--network=none` — no DNS, no loopback to host, no external network.
- Worktree bind-mounted at `/work` (rw) — the only writable host path.
- Nuget cache at `/root/.nuget/packages` (rw, shared `imp-nuget` volume).
- Resource limits (2 GB memory, 2 CPUs, 256 pids).

The cached nuget volume means `dotnet restore` works for packages you've already downloaded once; **packages the project hasn't adopted yet will fail to restore** — that's deliberate. Package-adoption is a judgment call that belongs in the parent (Claude Code), not in a cheap executor that reaches for random dependencies.

First cold-cache run pays the full nuget download; subsequent runs hit the volume.

## Project layout

```
imp/
├── Imp.csproj
├── Program.cs              # stdio MCP server + --ping / --build / --render-transcript CLI
├── McpTools.cs             # MCP-exposed tools (build, validate_contract, get_contract, …)
├── Executor.cs             # main tool-call loop, self-check, closeout phases
├── Contract.cs             # contract markdown parser + validator
├── BuildResult.cs          # proof-of-work DTO + JSON serializer
├── Tools.cs                # bash / read_file / write_file / apply_patch wiring, ExecutorState
├── GrepTool.cs             # regex content search
├── ApplyPatch.cs           # codex apply_patch parser + applier
├── SeekSequence.cs         # tolerant line-matching for apply_patch
├── CommandClassifier.cs    # danger-pattern pre-flight gate
├── NetworkEgressChecker.cs # network-tool pre-flight gate
├── DoomLoopDetector.cs     # stateful loop / repeated-failure detector
├── Todo.cs                 # session todo list (todo_read/todo_write)
├── Pricing.cs              # per-model token→USD estimator
├── SandboxConfig.cs        # Host/Docker sandbox config
├── TraceWriter.cs          # JSONL forensic sidecar
├── TranscriptRenderer.cs   # renders transcript.md from trace.jsonl
├── Worktree.cs             # git worktree management
├── Prompts.cs              # system-prompt loader
├── Providers.cs            # provider factory
├── Prompts/                # system-prompt templates (default, AzureFoundry, closeout)
├── Templates/              # MCP resource payloads (contract.md, proof-of-work.json)
├── sandbox/                # Dockerfile + build.sh for the execution sandbox
├── skills/imp.md       # Claude Code skill — install to ~/.claude/skills/
├── appsettings.example.json
├── CLAUDE.md               # developer notes (build gotchas, conventions)
├── docs/                   # mechanical reference — architecture + file formats
└── project/                # design docs + rolling TODO
```

## Documentation

Mechanical reference — how the pieces move, what the file formats are — lives under `docs/`:

- `docs/architecture.md` — request lifecycle, phases, state, tool plane, safety architecture, extension points
- `docs/formats.md` — contract markdown, proof-of-work JSON, JSONL trace, markdown transcript

Design decisions and roadmap — the "why" — live under `project/`:

- `BRIEF.md` — original design doc; the framing that seeded the repo
- `executor-v1-research.md` — v1 design decisions with source citations
- `bucket-a-survey.md` — primary-source survey of how shipping systems (Cursor, Aider, Codex, Devin, Kiro, Spec Kit, etc.) structure the planning-to-execution handoff
- `v2-plan.md` — phased path from alpha through Docker sandbox
- `spec-kit-integration.md` — exploratory doc on pairing imp with GitHub Spec Kit
- `TODO.md` — rolling work queue

## License

See `LICENSE`.
