---
kind: learning
title: Prefer multiple layered weak signals over one rigorous signal
created: 2026-05-10
updated: 2026-05-10
status: current
touches:
  files: [Safety/CommandClassifier.cs, Safety/NetworkEgressChecker.cs, Safety/DoomLoopDetector.cs]
  features: [design-philosophy, safety]
provenance:
  author: imp-gnome
topics: [design-philosophy]
---

# Weak signals compose

Project-level design preference: when faced with a tradeoff between
"one rigorous mechanism" and "multiple layered weak signals," lean
toward the layered weak signals. They compose cheaply, fail
independently, and degrade gracefully.

**Why:** rigorous mechanisms tend to be brittle (one missed edge
case fails the whole guarantee), expensive (parser/grammar/proof
work), and slow to iterate on. Weak signals can be added
incrementally, each catches a different failure mode, and a missed
case in one is often caught by another. The safety stack is the
canonical example: `CommandClassifier` (regex patterns), the Docker
sandbox (network=none + bind-mount), `NetworkEgressChecker`
(separate regex pass), and `DoomLoopDetector` (statistical, over
recent calls). Each is "weak" alone; together they're robust enough.

**How to apply:** when presenting design tradeoffs to the user, lead
with the pragmatic-multiple-weak-signals option, even if a more
rigorous alternative exists. The user will ask for rigor if they
want it; defaulting to it produces over-engineered solutions.
