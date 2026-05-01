# imp — developer notes

CLI tool that delegates rote, narrow-scoped coding work to a cheap,
slow executor (default: Azure GPT-5.1-codex-mini) running in a fresh
git worktree, and returns a structured proof-of-work. Invoked from
Claude Code via Bash + the `imp` skill in `skills/imp.md`.
See `project/BRIEF.md` for the architecture and intent.

Originally an MCP stdio server; rewritten as a CLI to escape MCP
subprocess opacity (no stderr capture by Claude Code), Windows DLL
locks, and restart-to-pick-up-tool-changes friction. See
`project/cli-plan.md` for the rewrite history.

## Build / run

```
dotnet build
dotnet run -- help                                 # subcommand reference
dotnet run -- ping [ProviderName]                  # smoke-test provider round-trip
dotnet run -- ping-tools [ProviderName]            # verify multi-turn tool-calling
dotnet run -- validate contracts/T-008-foo.md      # dry-run a contract
dotnet run -- build contracts/T-008-foo.md         # run a contract end-to-end
dotnet run -- review T-008                         # post-build bundle (no MCP, no worktree dive)
```

Config is loaded from `appsettings.json` — schema in
`appsettings.example.json`. `dotnet run` loads cwd's
`appsettings.json` first, then falls back to the one next to the
DLL.

For day-to-day Claude Code use, the parent invokes `imp` via
Bash. Each invocation is a fresh process — no long-lived state, no
DLL lock issues, no restart-to-pick-up-tool-changes.

## Layout

Flat. `*.cs` + `*.csproj` at repo root. `project/` holds markdown
design docs only. `Templates/` holds the contract skeleton and the
proof-of-work example (printed by `imp template <name>`).
`skills/imp.md` is the source of truth for how the parent model
should use imp — copy or symlink it into Claude Code's skills
directory.

## Logging

`ImpLog` writes to `<exe-dir>/imp.log` (next to the DLL) and
mirrors to stderr. Append-only, thread-safe, never throws. Each
`build` invocation logs every pre-worktree step, the executor
start/finish, and any rejection reason. Per-contract trace artefacts
still live at `<parent>/<repo>.worktrees/<T-NNN>.trace/` —
`trace.jsonl`, `transcript.md`, `proof-of-work.json`.

## Class names

The static surface methods live in `McpTools.cs` for historical
reasons (the file pre-dates the CLI rewrite). Rename is cosmetic and
deferred — the methods are now CLI handlers, not MCP tool entry
points.
