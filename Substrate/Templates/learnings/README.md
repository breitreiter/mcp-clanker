# Learnings

Discovered knowledge. Playtest feedback, simulation results, things
read in articles, things noticed during prior implementation, "huh
that didn't work" outcomes.

Learnings are not rules — they're awareness signals. They decay in
*relevance* (the area they touch may get rewritten), not in *truth*
(what was learned was true at the time). Old learnings still
matter for the "yeah but why" view; recent learnings shape current
caution.

The frame: "There's a snake in the back yard." Not a rule that you
want a snake there. Not a guarantee one's there now. But you'd be
careful walking through.

## Example shape

```yaml
---
kind: learning
title: <short learning title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human | imp-research:R-NNN | imp-build:R-NNN
relevance_horizon: YYYY-MM-DD       # optional — date past which to fade
topics: [<topic-slug>, ...]
---

# <Learning title>

<What was learned. Cite the source — playtest session, sim run,
article, prior commit, etc.>

## Implications
<What this means for current or future work, if anything.>
```

See `../_meta/conventions.md` for full conventions.
