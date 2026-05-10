---
kind: rule
title: No absolute local filesystem paths in source files
created: 2026-05-10
updated: 2026-05-10
status: current
touches:
  files: []
  features: [hygiene, portability]
provenance:
  author: human
enforces:
  - "**/*.cs"
  - "**/*.ts"
  - "**/*.tsx"
  - "**/*.js"
  - "**/*.jsx"
  - "**/*.py"
  - "**/*.go"
  - "**/*.rs"
---

# No absolute local filesystem paths in source files

Absolute paths like `/home/joseph/...` or `C:\Users\...` don't belong
in `.cs`, `.ts`, `.py`, or any other source file. Fine in markdown
docs, chat, plans, learnings.

**Why:** non-portable across machines and contributors; leaks
developer-specific layout into the codebase; makes diffs noisy when
people sync. The substrate doesn't have this constraint because it's
narrative; code does because code runs on other people's machines.

**How to apply:** if a path needs to be configurable, take it from
`IConfiguration`, an environment variable, or `AppContext.BaseDirectory`.
If a path is for an example, put it in a comment-marked example file
or in markdown.
