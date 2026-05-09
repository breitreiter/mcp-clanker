# Aspirations

What we're going for. Design intent. Code falls short by definition.
Internal contradictions are normal — they describe the design tension
space ("combat should be deep but intuitive").

Drift between aspiration and code is **informative**, not alarming.
Surfaces on the relevant concept page as the gap analysis.

Examples: tone-of-voice docs, "we want this to feel like X" notes,
philosophical principles, target experience.

## Example shape

```yaml
---
kind: aspiration
title: <short aspiration title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human
---

# <Aspiration title>

<What we're going for, in plain prose. Don't pretend this is a
contract; it's the direction.>

## Tensions
<If this aspiration includes deliberate contradictions, name them
here. They're features, not bugs.>
```

See `../_meta/conventions.md` for full conventions.
