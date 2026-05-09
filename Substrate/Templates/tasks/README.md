# Tasks

Tracking items: TODOs, fix-its, in-flight work that doesn't warrant
its own plan yet.

Tasks may be promoted to plans when they grow substantive scope (the
sweep imp will flag this). Until then, they live here as
small entries.

If you prefer a flat root `TODO.md` instead of per-topic task files,
set `tasks_path: TODO.md` in `../_meta/config.yaml`.

## Example shape

```yaml
---
kind: task
title: <short task title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human
topic: <topic-slug>          # optional
parent_plan: <plan-filename> # optional
---

# <Task title>

- [ ] <item>
- [ ] <item>
```

See `../_meta/conventions.md` for full conventions.
