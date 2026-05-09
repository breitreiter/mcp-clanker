# Archived plans

Plans that have concluded. Three states live here:

- `state: shipped` — done; lives in the codebase now. The plan doc is
  the canonical "yeah but why" record of how we got there.
- `state: shelved` — paused; might pick up later.
- `state: abandoned` — tried or evaluated; not pursuing. Kept for
  the lesson.

Archive entries are useful for history but are not authoritative on
current behavior. Lint / drift checks should not flag disagreement
between archived plans and current code as alarming.

When a plan moves here (especially `shipped` or `abandoned`),
consider whether a learning entry should be extracted — the plan
archive preserves the work; the learning captures what we'd carry
forward.

If the plan had a companion directory (`<slug>/`), it moves with the
`.md` — both end up here together.

## Frontmatter

Same as active plans, but with `state: shipped | shelved | abandoned`.

See `../../_meta/conventions.md` for full conventions.
