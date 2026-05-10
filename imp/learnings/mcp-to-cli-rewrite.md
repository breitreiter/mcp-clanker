---
kind: learning
title: imp was rewritten from MCP server to CLI to escape stderr opacity and DLL locks
created: 2026-05-10
updated: 2026-05-10
status: current
touches:
  files: [Program.cs, project/cli-plan.md]
  features: [history, tooling]
provenance:
  author: imp-gnome
topics: [history, mcp, cli]
---

# MCP→CLI rewrite

imp was originally an MCP stdio server. It now runs as a CLI invoked
from Claude Code via Bash plus the `imp` skill. The rewrite was driven
by three concrete pain points, not a preference:

1. **No stderr capture.** Claude Code can't see MCP subprocess stderr,
   so pre-worktree failures left no diagnostic trail in the parent
   conversation.
2. **Windows DLL locks.** A long-lived MCP process held .NET assembly
   locks that blocked rebuilds.
3. **Restart-to-pick-up-tool-changes.** Iterating on the tool surface
   meant restarting the MCP server every time, breaking flow.

**Why:** the CLI design dodges all three — each invocation is a fresh
process; no DLL lock; new code is picked up immediately; stderr is
visible in the parent's bash output.

**How to apply:** don't propose returning to a long-lived process
shape (MCP, daemon, server) without addressing all three pain points.
`ImpLog` writes to a file and mirrors to stderr precisely because
this rewrite traded stderr-visibility-via-process-shape for
stderr-visibility-via-explicit-mirroring. See `project/cli-plan.md`
for the full rewrite history.
