---
kind: learning
title: New dependency decisions are Opus territory; cheap executors fail closed
created: 2026-05-10
updated: 2026-05-10
status: current
touches:
  files: [Templates/contract.md]
  features: [executor-discipline, dependencies]
provenance:
  author: imp-gnome
topics: [executor-discipline, dependencies]
---

# New deps are Opus territory

The cheap build executor (Azure GPT-5.1-codex-mini today) must not
add new package dependencies — no `dotnet add package`, no
`npm install`, no `pip install`. Dependency decisions involve
license review, security posture, ecosystem fit, and long-term
maintenance — judgment work the parent (Opus/Sonnet) is competent at
and the cheap executor is not.

**Why:** a cheap executor adding deps is an unbounded blast radius.
A misplaced trust in a typo-squatted package, a transitive license
landmine, or a heavy framework imported "just to make this one
function easier" — none of these are worth the cost of letting
through.

**How to apply:** contracts authored for the cheap executor should
not require new deps. If a contract genuinely needs one, the parent
adds it before the build (or the contract gets rejected for a
redesign that uses existing deps). The classifier doesn't currently
block install commands — this is enforced via contract design and
prompt instruction, which is a softer signal that should harden over
time.
