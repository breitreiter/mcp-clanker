---
kind: plan
title: Embeddings primitive + tidy phase 2 (locate-existing-entry)
state: active
created: 2026-05-11
updated: 2026-05-11
touches:
  files: [Substrate/Tidy.cs, Infrastructure/Embeddings.cs, Prompts/tidy-triage.md, Prompts/tidy-draft.md]
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
- **Cache: `.imp/embeddings.jsonl`** (gitignored — see "Future
  direction" below for why not checked-in). One record per
  line: `{path, content_hash, vector}`, sorted by `path` on
  every write. Doc-level (one vector per entry). Dimension-
  locked to 4096 (Qwen3 8B). Stale entries (content_hash
  mismatch) re-embedded on next tidy run; missing entries
  embedded; orphaned entries (path no longer exists) pruned.
  Embedding input is `title\n\nbody` (title prepended as a
  header line; the substance lives in the body but the title
  helps recall against short-query embeddings).
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
- **Cache lifecycle.** Every tidy run begins with an index
  refresh — this is part of tidy's job, not a separate command.
  - Scan `imp/learnings/`, `imp/reference/`, `plans/`, `rules/`.
    For each, compute sha256 of body.
  - If cache miss or hash mismatch → mark for embedding.
  - If cache entry has no corresponding file → drop.
  - Batch all "mark for embedding" entries into a single
    `/v1/embeddings` request (OpenAI-compat endpoints accept
    `input: []`). One round-trip per refresh.
  - After tidy writes new/updated entries (note → patch or new
    file): append/replace their cache rows in the same run.
  - All writes are atomic rewrite (write `.tmp`, rename),
    sorted by path. Diff scopes to the changed rows only.
- **Serialization.** The Qwen3-8B endpoint at imp:8081 is
  single-threaded — no parallel requests. Within one tidy run
  that's automatic (sequential pipeline). Across runs: don't
  run two `imp tidy` invocations concurrently. If this bites
  us later, add a flock on `.imp/embeddings.jsonl`.

## Decisions

| # | Question | Decision |
|---|----------|----------|
| 1 | Cache location | `.imp/embeddings.jsonl`, **gitignored** (see "Future direction") |
| 2 | Model | Qwen3 Embedding 8B at `imp:8081`, 4096 dims |
| 3 | Fallback when imp:8081 unreachable | Tidy errors out — no hosted fallback |
| 4 | Granularity | Doc-level (one vector per entry). Revisit only if phase 2 misses on long multi-topic plans. |
| 5 | Embedding input | `title\n\nbody` (title prepended; body has the substance) |
| 6 | Top-K for locate | K=5, filtered by `kind`, no similarity threshold for v0. Let the locate LLM call discriminate. |
| 7 | Locate as separate call | Yes — 3-call pipeline (triage → locate → draft-or-patch). Separability win > one extra round-trip, and we don't trust the model enough to fold locate into draft yet. |
| 8 | Init seeding | Defer embedding to first tidy run. `imp init` stays network-dependency-free. |
| 9 | Cache write granularity | Atomic rewrite (write `.tmp`, rename), sorted by path. Cache is small enough that rewrite cost is irrelevant; sort-by-path keeps diffs scoped. |

Provider lock-in lives in `rules/embedding-provider.md`. The
cache is dimension- and provider-locked; mixing vector spaces
silently corrupts nearest-neighbor results, so this is a real
invariant, not a preference.

## Future direction (out of scope for v0, but informs decisions above)

**Pluggable providers, eventually.** Today's lock to Qwen3-8B at
imp:8081 is correct for the immediate case (one user, one box, one
home LAN) but will not survive contact with:
- The user wanting to run imp at work, where imp:8081 is unreachable
- External contributors who don't have access to the Strix Halo box
- Anyone else adopting the substrate model

A future plan will introduce provider selection (config-driven,
likely mirroring `Infrastructure/Providers.cs`'s chat-provider
pattern). At that point `rules/embedding-provider.md` becomes
per-deployment configuration, not a project-wide invariant.

This anticipated future is why the cache is **gitignored**, not
checked in:
- A checked-in cache is provider-specific by construction
  (dimension- and provider-locked). Once two users have different
  providers, the cache becomes per-user noise that fights over
  the same path.
- Cold-cache cost on fresh clone is one tidy run (~30s for a
  few hundred entries batched against local Qwen3-8B; longer
  but still bounded for a hosted provider).
- Moving from checked-in to gitignored later means a git-history
  rewrite and a transition period where users fight over cache
  state. Cheaper to put it in `.imp/` now.

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

## Open questions (resolved 2026-05-11 — see decisions table)

Original Q1–Q4 plus Q5 (embedding input) and Q6 (top-K) all
resolved. Q7 (locate call separation) resolved on trust grounds —
once the model has a track record of correct locate decisions we
can revisit folding it into draft. Q8 (init seeding) and Q9
(cache write granularity) resolved on minimum-surprise grounds.

## Acceptance

- `imp tidy` on a substrate with existing entries:
  - Refreshes the embeddings cache (embed new, re-embed changed,
    drop orphans) before processing the inbox.
  - For each inbox note: chooses create-new or update-existing
    based on top-K candidates + LLM decision. Path of any
    update is logged.
  - Updates existing entries with patched bodies; doesn't
    overwrite frontmatter.
- Cache is reproducible: blow away `.imp/embeddings.jsonl`,
  re-run tidy, get equivalent results (vectors deterministic up
  to model precision).
- `imp tidy` exits non-zero with a clear message when imp:8081
  is unreachable. No silent fallback.
- `rules/embedding-provider.md` is in place before this lands.
