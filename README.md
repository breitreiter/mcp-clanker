# mcp-clanker

C# stdio MCP server that hands structured coding tasks (*contracts*) from Claude Code to a cheaper executor model. Intended for rote, well-scoped work you don't want to burn Opus tokens on.

## Status

**Alpha. Not for real use.** The executor runs end-to-end against trivial contracts but is missing:

- Independent verification (closeout sub-agent)
- Safety gates (danger-pattern detection, network-egress restriction, doom-loop detection)
- Sandboxing (Docker)
- Several tools (`apply_patch`, `grep`, `list_dir`, `todo_*`)

The MCP `build` tool's description is deliberately deterrent so an Opus session doesn't reach for it proactively. Use only when explicitly invoked. See `project/v2-plan.md` for the path to "trust this enough to use at work."

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

The server exposes `ping`, `build`, and five stub tools. The `build` description is currently a deterrent string because the Claude Code skill that teaches proper use hasn't been authored yet (phase 2 of `project/v2-plan.md`). Until then, invoke only when you explicitly want to exercise it.

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
├── Prompts/                # system-prompt templates (default, AzureFoundry)
├── Templates/              # MCP resources (contract.md, proof-of-work.json)
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
