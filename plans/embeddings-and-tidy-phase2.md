---
kind: plan
title: Embeddings primitive + tidy phase 2 (locate-existing-entry)
state: exploring
created: 2026-05-11
updated: 2026-05-11
touches:
  files: [Substrate/Tidy.cs, Infrastructure/Providers.cs, Prompts/tidy-triage.md, Prompts/tidy-draft.md]
  features: [substrate, tidy, embeddings]
provenance:
  author: human
---

# Embeddings primitive + tidy phase 2

## Problem

Tidy v0a/v0b is create-only. Every inbox note becomes a new entry,
even when it's actually a refinement of, contradiction with, or
update to an existing one. Over time the substrate accumulates
near-duplicate entries on the same topic with no mechanism for
collapsing or superseding them. This is the "ton of random,
variously-accurate md files" failure mode that motivated the
substrate redesign in the first place — and tidy currently
reproduces it.

Phase 2 (locate-existing-entry-before-drafting) was deferred
pending embeddings. Qwen3 Embedding 8B is now running locally,
OpenAI-compatible, at `imp:8081`. Time to wire it up.

## Approach

Two pieces, shippable independently:

**1. Embedding primitive.** A thin `IEmbeddingGenerator` wrapper
under `Infrastructure/`, mirroring how `Providers.cs` builds chat
clients today. Single provider (Qwen3 Embedding 8B at imp:8081),
locked in via `rules/embedding-provider.md` to prevent silent
provider drift.

**2. Tidy phase 2.** Between triage and draft, embed the inbox
note, k-NN against the cached entry vectors, hand the top-K to a
"locate" LLM call that returns `create-new` or `update <path>`.
On update, the draft step gets the existing entry body as
context and produces a patched body instead of a fresh one.

## Components

- **`Infrastructure/Embeddings.cs`** — `EmbeddingClient` over the
  OpenAI SDK pointed at `imp:8081`. Reads provider config from
  `appsettings.json` the same way chat providers do.
- **Cache: `imp/_meta/embeddings.jsonl`** — checked-in, one
  record per line: `{path, content_hash, vector}`. Doc-level
  (one vector per entry). Dimension-locked to 4096 (Qwen3 8B).
  Stale entries (content_hash mismatch) re-embedded on next
  tidy run; missing entries embedded; orphaned entries (path no
  longer exists) pruned.
- **Phase 2 hook in `Tidy.cs`** — in `HandleEntryAsync`, between
  `TriageAsync` and `DraftBodyAsync`:
  1. Embed the note body.
  2. Cosine-similarity rank against all entries of the same
     `kind`. Take top-K (K=5).
  3. Call a new `LocateAsync` LLM step with the top-K candidates
     and the triage output. Returns `{decision: create|update,
     target_path?, rationale}`.
  4. If `update`, read the existing entry's body and pass it
     alongside the note body + triage to the draft step. Add a
     new prompt variant `tidy-draft-update.md` (or extend the
     existing prompt — open question) that produces a patched
     body, not a fresh one.
- **Cache lifecycle.**
  - Tidy startup: scan `imp/learnings/`, `imp/reference/`,
    `plans/`, `rules/`. For each, compute sha256 of body; if
    cache miss or hash mismatch, embed and update jsonl. If
    cache entry has no corresponding file, drop it.
  - After tidy writes new/updated entries: append/replace their
    cache rows in the same run, atomically (write to `.tmp`, rename).

## Decisions

| # | Question | Decision |
|---|----------|----------|
| 1 | Cache location | `imp/_meta/embeddings.jsonl`, checked in |
| 2 | Model | Qwen3 Embedding 8B at `imp:8081`, 4096 dims |
| 3 | Fallback when imp:8081 unreachable | Tidy errors out — no hosted fallback |
| 4 | Granularity | Doc-level (one vector per entry). Revisit only if phase 2 misses on long multi-topic plans. |

Provider lock-in lives in `rules/embedding-provider.md`. The
cache is dimension- and provider-locked; mixing vector spaces
silently corrupts nearest-neighbor results, so this is a real
invariant, not a preference.

## Out of scope

- **Drift detection** (`imp drift`) — separate plan. That's the
  re-evaluate-existing-entries-against-current-code sweep that
  stamps alignment-state. Embeddings primitive built here will
  be reused.
- **Code-side embeddings** (per-symbol or per-file) — needed
  for drift on entries with stale/empty `touches.files:`. Not
  this plan.
- **Chunking** — will revisit only if doc-level granularity
  proves insufficient on long plans.
- **`_index/` regeneration, layer-2 concepts** — separate work.

## Open questions

- **Similarity threshold.** Above what cosine score do we even
  show a candidate to the locate step? Calibrate empirically on
  the existing inbox + entries we have. Pre-decision: K=5
  always, let the LLM decide; threshold only kicks in if K=5 is
  noisy.
- **Locate prompt vs extended draft prompt.** Cleaner to split
  into a 3-call pipeline (triage → locate → draft/patch) at the
  cost of one extra round-trip per note. Probably worth it for
  separability.
- **`imp init` seeding.** When init lands seed entries, should it
  embed them immediately, or defer to first `imp tidy` run?
  Probably defer — keeps init dependency-free.
- **Cache write granularity.** Atomic rewrite vs append-only with
  periodic compaction. Lean rewrite — file is small.

## Acceptance

- `imp tidy` on a substrate with existing entries:
  - Refreshes the embeddings cache (embed new, re-embed changed,
    drop orphans) before processing the inbox.
  - For each inbox note: chooses create-new or update-existing
    based on top-K candidates + LLM decision. Path of any
    update is logged.
  - Updates existing entries with patched bodies; doesn't
    overwrite frontmatter.
- Cache is reproducible: blow away `imp/_meta/embeddings.jsonl`,
  re-run tidy, get equivalent results (vectors deterministic up
  to model precision).
- `imp tidy` exits non-zero with a clear message when imp:8081
  is unreachable. No silent fallback.
- `rules/embedding-provider.md` is in place before this lands.
