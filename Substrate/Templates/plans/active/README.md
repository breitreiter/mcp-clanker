# Active plans

The **primary working area** for in-flight work. Most new work in this
repo starts here — features, refactors, investigations, audits all
land as a plan in `state: exploring` and accumulate content as the
work progresses.

States in this directory:

- `state: exploring` — pre-decision. Research, options analysis,
  prior-art surveys, "should we even do this?" thinking. Often the
  longest-lived state.
- `state: active` — we've committed. Plan is firming up; work is
  underway.

When a plan concludes, it moves to `../archive/` with state
`shipped`, `shelved`, or `abandoned`.

## File-or-directory

A plan is **always** a single file: `<slug>.md`. The frontmatter and
plan narrative live there.

A plan **may also** have a companion directory `<slug>/` next to it
when the planning work outgrows one file — html/js prototypes,
scratch experiments, raw research dumps, screen mocks, anything. The
companion dir is free-form by design. No required structure. The
`.md` and `<slug>/` move together when state transitions.

## Example shape

```yaml
---
kind: plan
title: <plan title>
state: exploring
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human
companion_dir: false   # set true if you've created <slug>/ alongside
---

# <Plan title>

## Why we're looking at this
<seed of the idea>

## Prior art / research
<accumulates as you investigate>

## Options
<as the shape firms up>

## Decision
<once made; before this it's still exploration>

## Phases / milestones
- [ ] <step>

## Open questions
- <question>
```

Sections are suggestive — add and remove as the work demands.

See `../../_meta/conventions.md` for full conventions.
