# mcp-clanker — developer notes

Stdio MCP server (`nb-mcp`) that bridges Claude Code to nb's executor. See
`project/BRIEF.md` for the architecture and intent.

## Build / run

```
dotnet build
dotnet run -- --ping [ProviderName]   # smoke-test provider round-trip
dotnet run                             # start stdio MCP server (normally launched by Claude Code)
```

Claude Code launches the server via `dotnet run --project <repo>` (Debug
config). Config is loaded from `appsettings.json` — schema in
`appsettings.example.json`, mirrors nb's shape exactly so you can copy
`../nb/appsettings.json` wholesale.

## Gotcha: rebuilding while Claude Code holds the MCP server open

Claude Code runs the server as a long-lived stdio subprocess pinned to
`bin/Debug/net8.0/McpClanker.dll`.

- **Linux (and macOS):** no problem. `dotnet build` / `dotnet run` happily
  overwrite the on-disk DLL; the running process keeps executing its
  in-memory image until it exits. New launches pick up the new bytes.
- **Windows:** the DLL is locked while the server runs. `dotnet build`
  will fail with MSB3027 / "file in use." Either:
  - Stop the Claude Code session (or `/mcp` disconnect) before building, or
  - Re-register the MCP server against **Release** so dev builds (Debug)
    don't collide: change the Claude Code MCP command to
    `dotnet run --configuration Release --project <repo>`, and build with
    `dotnet build` (Debug) separately while iterating. Run a
    `dotnet publish -c Release` to refresh what the running server uses.

Tool changes aren't picked up until the MCP subprocess restarts
regardless of OS — exit Claude Code (or `/mcp` reconnect) after changing
tool signatures.

## Layout

Flat. `*.cs` + `*.csproj` at root. `project/` holds markdown design docs
only. `Templates/` holds MCP resource payloads (contract skeleton,
proof-of-work example).
