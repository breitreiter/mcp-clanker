# Technical documentation

Mechanical reference for readers who want to understand or extend clanker. Complements, not replaces, the design docs under `project/` (which answer "why" and "where next").

## In this directory

- **[architecture.md](architecture.md)** — how a `build()` call moves through the system. Request lifecycle, phases (main loop / self-check / closeout), execution state, tool plane, safety architecture, worktree layout, process boundaries, extension points.
- **[formats.md](formats.md)** — the four on-disk / on-wire formats. Contract markdown (input), proof-of-work JSON (output), JSONL trace (forensic sidecar), markdown transcript (human-readable render).

## In `project/` (design + history)

- `BRIEF.md` — original framing document
- `executor-v1-research.md` — v1 design decisions with source citations
- `bucket-a-survey.md` — primary-source scan of adjacent systems (Cursor, Aider, Codex, Devin, Kiro, Spec Kit, …)
- `v2-plan.md` — the phased path this repo has worked through
- `spec-kit-integration.md` — exploratory doc on pairing clanker with GitHub Spec Kit
- `TODO.md` — rolling work queue, annotated with what's shipped

## Which doc for which question

| If you want to… | Read |
|---|---|
| Understand what `build()` does, step by step | architecture.md § Request lifecycle |
| Write a consumer that reads POW JSON | formats.md § Proof-of-work |
| Add a new tool the executor can call | architecture.md § Extension points |
| Write a dashboard that streams traces | formats.md § Trace |
| Understand how closeout catches lies | architecture.md § Phases 4c + § Safety architecture |
| Know which `blocked_question.category` fires when | architecture.md § Category matrix |
| Understand the worktree + trace directory layout | architecture.md § Worktree + trace layout |
| Understand why any of this is shaped the way it is | `project/BRIEF.md` + `project/executor-v1-research.md` |
