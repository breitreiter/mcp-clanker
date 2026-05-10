---
kind: rule
title: Imp owns the auto-commit; the executor model never commits
created: 2026-05-10
updated: 2026-05-10
status: current
touches:
  files: [Safety/CommandClassifier.cs, Build/LifecycleCommands.cs]
  features: [safety, executor-discipline]
provenance:
  author: human
enforces:
  - "Safety/CommandClassifier.cs"
---

# Imp owns the auto-commit

The executor model must never run `git add` or `git commit` during a
build. `LifecycleCommands.Build` is the authoritative committer; it
auto-commits on evaluator sign-off, once, with a known shape. The
model committing creates half-staged trees and double-commits.

**Why:** imp's contract with the parent agent is a single
proof-of-work commit per build, on a known branch
(`contract/<task-id>`). If the model commits inline, the auto-commit
no-ops or duplicates, and the proof-of-work no longer matches the
diff.

**How to apply:** `Safety/CommandClassifier.cs` blocks `git add` and
`git commit` always-on, regardless of sandbox mode. Don't add any
code path that bypasses this. If a future flow needs intermediate
commits, that's a contract design change, not a classifier hole.
