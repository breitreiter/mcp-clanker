# mcp-clanker

C# stdio MCP server that hands structured coding tasks (*contracts*) from Claude Code to a cheaper executor model. Intended for rote, well-scoped work you don't want to burn Opus tokens on.

## Status

**Beta.** v2-plan phases 1–6 have all shipped and been validated. The executor runs against medium contracts with:

- Safety gates (danger-pattern classifier, network-egress gate, doom-loop detector)
- Independent closeout verification (fresh-context reviewer with read-only tools; overrides self-report and demotes `success` → `failure` when any bullet flunks)
- Full toolkit (`bash`, `read_file`, `write_file`, `apply_patch`, `grep`, `list_dir`, `todo_read`, `todo_write`)
- Docker sandbox (opt-in via `Sandbox.Mode=Docker` in appsettings)

Remaining gaps before GA: a `**Allowed network:**` contract declaration (so contracts that need network can opt in), config-driven `MaxOutputTokens` per provider, Windows shell detection, MCP-server hot-reload shim for faster iteration. See `project/TODO.md`.

## What it does

1. Claude Code (Opus-scale planning) writes a structured **contract** — a markdown file describing a focused change: goal, scope (explicit file list), contract body, acceptance criteria, non-goals.
2. `mcp-clanker`'s `build` tool parses the contract, creates a fresh git **worktree** on a new branch (`contract/T-NNN`) in the target repo, and runs an **executor** — a tool-calling loop against the configured provider (primary target: Azure Foundry / gpt-5.1-codex-mini).
3. The executor uses a minimal tool set (`bash`, `read_file`, `write_file` for v1) to make the changes inside the worktree.
4. Returns a **proof-of-work** JSON with terminal state, tool-call count, files changed, and model notes. A full execution **trace** (JSONL) lands in a sidecar directory for forensic reading when proof-of-work isn't enough.

## Why

The economic model: plan with Opus (expensive, scarce), grind with a cheap model (Azure OpenAI tokens, effectively unlimited), and keep human review cost bounded by a structured, scannable proof-of-work. The parent never has to read the full transcript of the child's work — only the shape of what was attempted and whether it stuck.

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
- Credentials for at least one supported provider.

## Setup

```bash
git clone https://github.com/breitreiter/mcp-clanker.git
cd mcp-clanker
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

A contract template is at `Templates/contract.md`. The build command creates a worktree at `<parent>/<repo>.worktrees/<task-id>/` and a trace at `<parent>/<repo>.worktrees/<task-id>.trace/trace.jsonl`; neither is cleaned up automatically.

### Via Claude Code MCP

Register the server in your Claude Code MCP config:

```json
{
  "mcpServers": {
    "mcp-clanker": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/mcp-clanker"]
    }
  }
}
```

The server exposes `ping`, `build`, and five stub tools.

### Build the Docker sandbox (optional, recommended for real use)

Clanker defaults to `Sandbox.Mode=Host`, which runs `bash` directly on the host — convenient during development, but no isolation beyond the application-layer safety gates. For production-like use, build the sandbox image:

```bash
./sandbox/build.sh
```

That builds `clanker-sandbox:latest` (dotnet SDK 8 + git) and creates the `clanker-nuget` Docker volume used to share the package cache across contracts. Then flip `Sandbox.Mode` to `"Docker"` in `appsettings.json`.

In Docker mode, each `bash` call runs in a throwaway container with `--network=none` and the worktree bind-mounted at `/work`. The package cache volume means `dotnet restore` works for packages you've already downloaded once; **packages the project hasn't adopted yet will fail to restore** — that's deliberate. The parent (Claude Code) decides what packages a project should adopt, not clanker's executor.

### Install the Claude Code skill

The `skills/clanker.md` file in this repo teaches Claude Code how to decide whether to delegate, write contracts, and interpret proof-of-work. Install it once at user scope so it's active in any repo where the MCP server is registered:

```bash
mkdir -p ~/.claude/skills
cp skills/clanker.md ~/.claude/skills/clanker.md
# or symlink to pick up edits: ln -s "$(pwd)/skills/clanker.md" ~/.claude/skills/clanker.md
```

Restart the Claude Code session after installing so the skill is picked up.

## Project layout

```
mcp-clanker/
├── McpClanker.csproj
├── Program.cs              # stdio MCP server + --ping / --ping-tools / --build CLI modes
├── McpTools.cs             # MCP-exposed tools
├── Executor.cs             # recursive tool-call loop
├── Contract.cs             # markdown parser + validator
├── BuildResult.cs          # proof-of-work DTO + JSON serializer
├── Tools.cs                # bash / read_file / write_file
├── Providers.cs            # provider factory
├── Prompts.cs              # system-prompt loader
├── TraceWriter.cs          # JSONL forensic sidecar
├── Worktree.cs             # git worktree management
├── Prompts/                # system-prompt templates (default, AzureFoundry, closeout)
├── Templates/              # MCP resources (contract.md, proof-of-work.json)
├── sandbox/                # Dockerfile + build.sh for the execution sandbox
├── skills/clanker.md       # Claude Code skill — install to ~/.claude/skills/
├── appsettings.example.json
├── CLAUDE.md               # developer notes (build gotchas, conventions)
└── project/                # design docs
```

## Documentation

Design decisions and roadmap live under `project/`:

- `BRIEF.md` — original design doc; the framing that seeded the repo
- `executor-v1-research.md` — v1 design decisions with source citations
- `bucket-a-survey.md` — primary-source survey of how shipping systems (Cursor, Aider, Codex, Devin, Kiro, Spec Kit, etc.) structure the planning-to-execution handoff
- `v2-plan.md` — phased path from current alpha to "use at work"
- `TODO.md` — rolling work queue

## License

See `LICENSE`.
