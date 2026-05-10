---
kind: learning
title: imp does deterministic file ops; skills do synthesis and judgment
created: 2026-05-10
updated: 2026-05-10
status: current
touches:
  files: [skills/imp.md, skills/imp-promote.md, Substrate/ProjectInit.cs]
  features: [architecture, boundaries]
provenance:
  author: imp-gnome
topics: [architecture, skills]
---

# Static vs synthesis boundary

The split between what lives in `imp` (the CLI) and what lives in a
skill (markdown loaded by Claude Code) is **synthesis-vs-static**,
not substrate-vs-primitive. If the operation is deterministic — file
copies, dir scaffolds, frontmatter parses, manifest reads — it
belongs in imp, where it runs in milliseconds without burning model
budget. If the operation requires synthesis, judgment, or interactive
review with the user, it belongs in a skill.

**Why:** the cheap-fast/expensive-slow split is the whole point of
imp's architecture (parent Claude orchestrates; cheap qwen executes).
Putting deterministic ops in skills wastes parent context and
latency. Putting synthesis in imp produces low-quality output because
the cheap executor isn't good at it.

**How to apply:** when designing a new feature, ask "is this
deterministic or does it need judgment?" — that decides whether the
implementation goes in `Substrate/`/`Build/`/etc. or in
`skills/<name>.md`. `imp init` is on the static side (template copy);
`imp tidy` straddles (gnome work uses model judgment, but the dir
walk is static); `/imp-promote` is fully synthesis (review proposals
with user input).
