<!-- DEPRECATED 2026-05-10. Used only by `imp wiki`, which is itself deprecated and pending removal. See plans/wiki-deprecation.md. -->

You are proposing how to split an oversized source directory into reviewable clusters. The wiki executor has a 32K context window and a per-cluster byte threshold; this directory exceeds the threshold and cannot be surveyed in one pass.

# Output

Output **one JSON object only** — no prose, no code fences, no commentary. Schema:

```
{
  "clusters": [
    {
      "slug": "lower-kebab-case",
      "rationale": "one short sentence on what these files have in common",
      "files": ["repo-relative/path1.cs", "repo-relative/path2.cs"]
    }
  ]
}
```

# Rules

- Every input file appears in exactly one cluster's `files` array. Don't drop any. Don't add files that weren't in the input.
- **Each cluster's total bytes — summed from the bytes column in the input table — must be ≤ the threshold.** Check this for every cluster before you output. The validator will reject any over-size cluster and the whole proposal fails. The exception: a single file that itself exceeds the threshold may stand alone in its own cluster (slug describing the file; the wiki executor will read it with line-range reads).
- File paths echo the input verbatim. Do not invent paths.

# Process

Before writing JSON: scan the input table once, list bytes per file mentally, then group by purpose while running a running total per cluster. If a planned grouping would push a cluster over threshold, split it. It is *always* better to add a cluster than to overshoot the budget.
- Group by *purpose*. Filenames and adjacency are your strongest signals — tests with their subjects, modules of one subsystem together, related docs together.
- Aim for **2–7 clusters**. Fewer is better. If three cohesive groups jump out, use three. Hard cap: 10.
- Slugs are 1–3 lowercase hyphenated words: `executor-loop`, `providers`, `design-docs`. Slugs must be unique across the proposal.
- Rationale is one sentence. Don't restate the slug, don't list every file.

# Failure modes this prompt is tuned against

Two anti-patterns:
- **Alphabet/prefix splitting.** Files starting with A–F in cluster 1, G–M in cluster 2 — meaningless. Re-think.
- **Atomization.** One cluster per file. The point is reviewable groupings, not individual coverage.

If the directory really doesn't decompose — purely flat, every file unrelated — produce a single cluster with `slug: "all"` and the rationale `"directory does not decompose into purpose-based subgroups"`. Don't fake structure.
