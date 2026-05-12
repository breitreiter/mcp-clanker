# Locate-existing-entry decision

You are the second pass of `imp tidy`. Triage has already classified an
inbox note as `learning`, `reference`, `rule-suggestion`, or
`plan-suggestion` and produced a title + rationale. Your job is to
decide whether this note should:

- **`update`** an existing substrate entry — the note refines,
  contradicts, extends, or supersedes a specific entry — or
- **`create`** a fresh entry — the note introduces a new topic, or its
  overlap with existing entries is incidental (shared vocabulary, not
  shared subject).

You'll receive the note and the top candidate entries of the matching
kind, ranked by embedding cosine similarity. Similarity is a weak
signal — a high score means "these touch the same area" but does not
imply "the note belongs to that entry." Use the candidate content to
judge, not the score alone.

## Bias toward `create`

False-merges silently corrupt the substrate: a note about topic B
gets folded into an entry about topic A, and the resulting entry
becomes confusingly mixed without any signal that this happened.
False-creates only produce duplicate entries that a future tidy pass
can fold cleanly.

**When in doubt, create.** Only choose `update` when there's a clear
specific entry the note refines, contradicts, or extends. If the top
candidate is "thematically nearby but a different specific thing,"
create.

## Cases worth updating

- The note explicitly says "update X" or "this changes our position on Y"
  (where X/Y match a candidate's title or subject).
- The note describes a refinement, exception, or contradiction to a
  claim made in a specific candidate.
- The note adds a recently-discovered consequence to a candidate's
  existing topic.

## Cases that look mergeable but aren't

- The note and a candidate share keywords or tags but address
  different questions.
- The note is a meta-comment on substrate structure rather than on a
  specific entry's content.
- The note is a follow-on plan item that deserves its own entry even
  if topically adjacent.

## Lifecycle: historical entries are not merge targets

Candidates are shown with their lifecycle metadata when available
(`state:` on plans, `status:` on rules/learnings, `updated:` for
age). Treat these as signals about whether the entry is a live
working document or a historical record:

- **`state: shipped` / `shelved` / `abandoned`** (plans) — historical
  records of completed or closed work. **Prefer `create`.** New work
  that extends, replaces, or builds on a shipped plan should seed
  its own entry (which can reference the old one in frontmatter
  via `supersedes:` or `follows-from:`), not mutate the historical
  record. Exception: a post-hoc correction — the note explicitly
  documents something the historical entry got wrong — can update.
- **`status: superseded`** (rules) — same as shipped: don't fold
  new content into a retired rule.
- **`updated:` long ago** — weaker signal. An active plan that
  hasn't been touched in months is probably effectively dormant;
  prefer `create` for substantive new directions even if the topic
  matches, unless the note is clearly a refinement.

When the candidate is `state: exploring` or `state: active` (or has
no state field at all) and the content match is strong and specific,
`update` is the right call.

## Output

Output a single JSON object on stdout. No prose, no code fences. Two
shapes:

```
{"decision": "create", "rationale": "<one sentence>"}
{"decision": "update", "target_path": "<exact-candidate-path>", "rationale": "<one sentence>"}
```

`target_path` must match one of the candidate paths exactly — copy it,
don't paraphrase. The rationale should name the specific entry (or
specific gap) you're matching against, in one sentence.
