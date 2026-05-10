# imp wiki — feature plan

> **DEPRECATED 2026-05-10.** `imp wiki` is superseded by the substrate
> (`imp/`, maintained by `imp tidy`). This doc is kept for reference
> only — do not act on it. See `plans/wiki-deprecation.md` for the
> rationale and removal trigger.

> Status: v0 + adaptive splitting shipped (2026-05-09). Build-order
> items 1–8, 11a (model-rendered README), and 11 (adaptive splitting)
> are landed. Oversized dirs are now split via an orchestrator
> (Haiku 4.5) cluster proposal validated and bin-packed in code,
> dispatched as N research runs against the wiki executor (Qwen).
> Validated end-to-end on the imp repo: `project/` (217KB, 12 files)
> split into 8 cohesive clusters with all pages generated and the
> README synthesized over the full coverage. Per-target brief
> tailoring (11b), drift detection (10), parallel dispatch (12), and
> --check-drift (13) remain post-v0; the `*(later)*` markers in the
> build order below are still the canonical backlog.

A third top-level command alongside `imp build` and `imp research`.
Walks a target tree (default: repo root), runs research mode against
each directory in scope, and renders the resulting reports into a
mirrored markdown wiki at `wiki/<path>.md`. Pages are committed with
the codebase. Designed for nightly re-runs: incremental, idempotent
when nothing has changed, and budget-aware on a 32K context executor.

The motivating long-term goal is twofold:

1. **Keep a living wiki of the repo.** Pages stay in sync with
   the code so a parent agent (or a human) can orient quickly without
   reading source. Each page is a short, citation-anchored survey of
   what the directory contains.
2. **Flag drift from design docs for human review.** Once the wiki
   exists, a downstream `imp drift` command can ask "where do these
   pages disagree with `project/*.md`?" — drift is what research
   mode's existing `Conflict` schema is shaped for.

`imp wiki` builds the wiki. `imp drift` consumes it. This plan
covers `wiki` end-to-end and sketches `drift` only enough to confirm
the artefacts feed it cleanly.

## Goals

- Whole-tree default. `imp wiki` with no arguments walks the repo
  root and produces (or updates) a wiki page for every in-scope
  directory.
- Incremental. A nightly re-run skips any directory whose source
  tree hasn't changed since its page was last written.
- Budget-bounded per run. Each research run stays inside a tighter
  tool budget than ad-hoc research, sized for a 32K context
  executor (Qwen Coder on Strix Halo, today).
- Committable artefacts. Pages live at `wiki/<mirrored-path>.md`
  by default — out of the way of build-system file sweeps, but
  visible in the tree and reviewable in PRs.
- Agent-optimized output. Pages are short, structured, and
  citation-heavy. A parent agent reading `wiki/<path>.md` should
  get the same factual coverage it would get from skimming the
  directory, at a fraction of the tokens.
- Good-citizen behavior. The orchestrator never re-runs work that
  didn't need redoing. Layered cache keys (per-page tree SHA,
  per-invocation manifest, per-run path dedupe) are intentionally
  redundant.

## Non-goals

- Not adaptive in v0. Per-directory is the unit; oversized
  directories produce a stub page flagging the gap, not a split run.
  Adaptive splitting lands later.
- Not a code-quality reviewer. `wiki` describes what the code says it
  does, not what it should do. `drift` is the downstream comparator;
  `review` already exists for change-time review.
- Not a renderer for arbitrary reports. The renderer is bespoke to
  the wiki mode's report shape and the page layout. Generic
  research-report-to-markdown is out of scope.
- Not multi-repo. One repo per invocation. Cross-repo wiki linking
  is not in scope.

## Why now / reuse signals

Research mode is the load-bearing dependency, and it's already there:

- `Modes.cs:76` (`BuildFsMode`) defines the exact (read-only,
  no-network) profile the wiki command needs. The wiki mode is `fs` with a
  survey-tuned system prompt.
- `ResearchReport` (`ResearchReport.cs:18`) already carries
  `synthesis`, `findings[]` with file citations, `coverage`,
  `conflicts[]`, and `follow_ups[]` — a wiki page is essentially a
  rendering of this struct, not a new schema.
- `ResearchArchive.WriteReport` (`ResearchArchive.cs`) writes
  `report.json` per run; the renderer's input is exactly this file.
- `Research.RunAsync` (`Research.cs:32`) is the entry point the
  orchestrator calls per directory. No new executor scaffolding.

The new code is the orchestrator (walk, plan, dispatch, render) and
the renderer (`report.json` → `wiki/<path>.md`). Both are pure C#
around the existing research loop.

## Top-level shape

```
imp wiki [path]                  # default: repo root
imp wiki src/foo                 # subtree only
imp wiki --full                  # ignore SHA cache, regenerate everything
imp wiki --dry-run               # plan only: print which dirs would run
imp wiki --check-drift           # after generation, run drift command (later)
```

- Single argument: the target path (relative or absolute, must be
  inside the repo). Defaults to the repo root.
- Walks the target tree, plans per-directory runs, dispatches
  research, renders into `wiki/`.
- Long-running. Time scales with stale-dir count × per-run budget.
- Stdout: a single `WikiResult` envelope summarizing the run —
  `{run_id, repo_root, pages_written, pages_skipped_unchanged,
  pages_oversized_stub, pages_failed, archive_dir, wall_seconds,
  estimated_cost_usd}`. Per-page detail lives in the archive.

## Wiki layout

Pages mirror the source tree at `wiki/<relative-path>.md`. A
directory `src/Foo/` maps to `wiki/src/Foo.md`. The repo root maps
to `wiki/README.md` (also serves as the wiki index). Mirrored
placement avoids:

- Build-system file sweeps (`<Compile Include="**/*.cs">`, pytest
  collection, webpack globs) that pick up stray `.md` files in
  source dirs.
- Diff noise — wiki updates don't show up in source-file change
  history.
- Co-location locks — agents browsing source dirs don't see the
  wiki entry beside the file, but the imp skill teaches them
  where to look. This is a deliberate tradeoff.

The wiki location is configurable (`Wiki:Dir`, default
`wiki/`). Consumers who want it ephemeral gitignore it; consumers
who want it as a deliverable commit it. imp doesn't touch
`.gitignore`.

### Page shape

Each page has YAML frontmatter and a fixed body skeleton. Both
exist for agent legibility — frontmatter is structured and cheap
to parse, the body is structured and cheap to skim.

```markdown
---
source_path: src/Foo
source_tree_sha: a06c4d3f8e...
source_files_count: 12
source_bytes: 18432
generated_at: 2026-05-08T03:14:22Z
generator_version: 0
mode: fs
model: qwen-coder-30b
research_id: R-2026-05-08-src-foo-a06c4d3
status: generated    # generated | oversized | failed
---

# src/Foo

> Single-paragraph synthesis lifted verbatim from the research
> report. Direct, no "I found that..." framing.

## Contents

- `Bar.cs` — short claim about what Bar does, with a citation.
  [Bar.cs:12-44](../../src/Foo/Bar.cs#L12-L44)
- `Baz.cs` — ...

## Key types and entrypoints

Findings the executor flagged as load-bearing, rendered as
bullets with citations and one-sentence reasoning each.

## Cross-references

Findings whose citations point outside `src/Foo`. Each link
points to the wiki page for the referenced path when one exists,
falling back to the source file otherwise.

- Resolves contracts via [Contract.cs:154](../../Contract.cs#L154);
  see also [wiki/Contract.md](../../wiki/Contract.md).

## Open questions

- Coverage gaps: items from `report.coverage.gaps` and
  `report.follow_ups`.

## Conflicts

(Section omitted when `report.conflicts` is empty.)
```

Body sections map 1:1 to fields in `ResearchReport`. The renderer is
deterministic and pure — no model judgment is added at render time.
All model judgment lives in the research run that produced
`report.json`.

### `wiki/README.md` (the index)

After all per-dir runs complete, the orchestrator regenerates
`wiki/README.md` with:

- A tree of generated pages, one line each, with the synthesis
  summary truncated to a single line.
- Per-row: last-generated timestamp, status, link to the page.
- A footer noting the run id and total coverage.

The index is a deterministic render over the page frontmatter — it
does not call the executor.

## Orchestration

```
1. Resolve repo root, target subtree, configured wiki dir.
2. Walk the target tree:
     - skip ignored dirs (.git, wiki/, build outputs, .imp/, plus
       a Wiki:Ignore glob list from config)
     - for each remaining dir, compute scope-tree-sha:
         git ls-tree HEAD <relative-path> | sha256
       (For an unstaged WIP repo, falls back to a content-hash
       walk; flagged in frontmatter via worktree_dirty: true.)
3. Plan: for each dir, decide one of
     a. SKIP — wiki page exists, frontmatter source_tree_sha matches.
     b. STUB — total source bytes > Wiki:MaxDirBytes (default ~40KB).
        Write a stub page; record in manifest as oversized.
     c. RUN — dispatch research run, render result.
   Plan is written to <repo>.wikis/W-NNN-<slug>/manifest.json
   before any RUN dispatches, so a crashed/killed wiki run resumes
   cleanly via --resume.
4. Execute the plan:
     - For each RUN target, call Research.RunAsync with mode=wiki
       (a survey-tuned variant of fs; see below) and a per-target
       brief whose objective fixes the page's scope.
     - Render report.json → wiki/<path>.md.
     - Update manifest entry: pending → done.
5. Regenerate wiki/README.md from current frontmatter across all
   pages.
6. Emit WikiResult envelope to stdout.
```

### Cache layers (good-citizen behavior)

Three intentionally redundant signals prevent repeat work:

1. **Per-page `source_tree_sha` in frontmatter.** Authoritative
   per-page cache key. Computed via `git ls-tree HEAD <path>`. A
   nightly re-run reads the existing page's frontmatter and skips
   if the SHA matches.
2. **Per-invocation manifest at `<repo>.wikis/W-NNN/manifest.json`.**
   Written before any dispatch. Crash-resume via
   `imp wiki --resume W-NNN` re-reads the manifest and skips
   already-done entries.
3. **Per-run path dedupe.** The orchestrator walks once and
   canonicalises paths so a target dir is never queued twice
   within a single invocation.

The SHA cache is the load-bearing one; the manifest is for crash
recovery; the dedupe is for orchestrator-bug defence-in-depth.

### Oversized stub

When a directory's total source bytes exceed `Wiki:MaxDirBytes`
(default 40960 — a generous fit for 32K context plus tool overhead
and report output), the orchestrator skips dispatching and writes:

```markdown
---
source_path: src/BigDir
source_tree_sha: ...
source_bytes: 87234
status: oversized
threshold: 40960
generated_at: 2026-05-08T...
---

# src/BigDir

No wiki page was generated for this directory: total source bytes (87234) exceed
the v0 threshold (40960). Adaptive splitting will land in a future
version. For now, run `imp wiki src/BigDir/<subdir>` against
subdirectories individually if you need coverage here.
```

Stub pages are honest about the gap and visible in the wiki index.
They give a punch-list for adaptive support without lying about
coverage. Single-file dirs whose one file exceeds the threshold get
the same treatment.

`Wiki:MaxDirBytes` excludes generated/binary files: anything matched
by `.gitignore`, anything under a configured exclude-glob list, and
files whose extension isn't on a small text-like allowlist
(`.cs`, `.md`, `.json`, `.yaml`, `.ts`, `.tsx`, `.py`, `.go`,
`.rs`, `.sh`, `.toml`, ...). The allowlist is configurable.

## The `wiki` mode

A new mode registered alongside `fs` in `Modes.cs`:

- **Toolset:** identical to `fs` — `read_file`, `grep`, `list_dir`.
- **Sandbox profile:** identical to `fs` — read-only mount,
  no network, no subprocess.
- **Finish tool:** the existing `finish_research` tool with no
  changes. The report shape is already what the renderer wants.
- **System prompt:** new (`Prompts/research-fs-wiki.md`),
  survey-tuned. Differs from `research-fs.md` in steering, not
  capability:
    - Frame the task as "describe this directory for a wiki page,"
      not "answer a question."
    - Bias `findings[]` toward per-file claims (one finding per
      meaningful file) plus a small set of cross-cutting findings.
    - Bias `coverage.gaps` toward "files I didn't open and why" so
      stub-eligibility decisions are visible.
    - Suppress `conflicts[]` (no conflicts expected at generation time;
      drift detection is a separate command).
    - Cap `synthesis` length explicitly so pages stay terse.

The orchestrator constructs the brief in memory and hands it to
`Research.RunAsync` via the existing `--brief` path (or a new
in-memory equivalent — see Tradeoffs). Per-target brief shape:

```yaml
objective: |
  Produce a directory survey of <relative-path>, suitable for a
  wiki page that describes the contents of this directory to a
  reader who has not seen the source.
expected_output:
  - synthesis (<= 80 words)
  - findings[] (one per meaningful file, plus cross-cutting findings)
  - coverage (explored, not_explored, gaps)
key_questions:
  - What does each file in this directory do?
  - What are the entrypoints into this directory's code from
    elsewhere in the repo?
  - What are the load-bearing types or functions?
suggested_sources:
  - <relative-path>/<file-1>
  - <relative-path>/<file-2>
  ...
scope_boundaries:
  - Do not read files outside <relative-path> except to resolve a
    cross-reference. Do not exceed Wiki:ToolBudget tool calls.
```

## Orchestrator model role

A third model role alongside coding (build executor) and research:
**orchestration** (`Wiki:Provider` / `Wiki:Model`). v0 doesn't call this
model anywhere — the orchestrator code is deterministic C# — but the
config surface lands now so post-v0 features that need it can wire up
without re-shaping config later.

Why a separate role: the existing two roles don't fit. The coding
model is tuned for narrow, well-specified contracts in a worktree
(Codex mini today); the research model answers a single question on a
tight tool budget against a 32K context (Qwen Coder local today).
Orchestration is neither — it's planning, routing, and synthesis
across many small reports. It needs context tolerance (handling N
reports plus tree state), judgment (scope decisions, coverage
triage), and is too cross-cutting for the cheap research executor
without needing build-grade code-correctness chops. Sonnet or
GPT-5-mini class is the natural fit.

Decision points where the orchestrator model gets called (all
post-v0):

- **Adaptive splitting.** When a directory exceeds `Wiki:MaxDirBytes`,
  the model is handed the file manifest plus per-file size and
  proposes a clustering ("split into ≤40KB clusters by language,
  keeping `Foo.cs` and `FooTests.cs` together"). Replaces the v0 stub.
- **Per-target brief tailoring.** Instead of a templated brief, the
  model writes a brief shaped by sibling/parent context — what the
  parent dir's wiki page already says, what the dir's filenames hint
  at, recent commit subjects touching the dir.
- **Drift triage.** After `imp drift` runs research and produces a
  flat `Conflict[]`, the orchestrator model groups conflicts into
  actionable buckets and writes the structured `wiki/_drift.md`.
- **Repo-level synthesis.** `wiki/README.md` becomes a real overview
  ("here's how this codebase is organized; here are the load-bearing
  subsystems") rather than a deterministic tree dump of page titles.
- **Coverage triage.** When a research run returns with a stub-eligible
  gap or weak coverage, the model decides whether to re-dispatch with
  a refined brief or accept the gap.

Light, not heavy. The orchestrator stays a C# code object that calls
the model at these specific points, not an agentic loop with
`dispatch_research` and `write_wiki_page` as tools. Light is
debuggable, easy to budget-cap per call site, and keeps the loop
logic readable. If multiple orchestration capabilities ever need to
compose in a single loop, the decision-point seams are exactly where
heavy-mode tool boundaries would land — the refactor follows
naturally.

## Context discipline (32K executor)

The dominant constraint. Per-run defences, layered:

- **Tighter tool budget.** New `Wiki:ToolBudget` (default 10),
  separate from `Research:ToolBudget` (default 20-ish, ad-hoc
  questions). Wiki runs are scope-bounded, so ten reads + a finish
  call should be plenty for most directories.
- **Per-call read caps.** `read_file` already truncates; the wiki
  mode's system prompt reminds the executor to read targeted line
  ranges, not whole files, when a file exceeds N lines.
- **No prior-page feed-in.** When regenerating a stale page, the
  executor does not see the previous wiki content. Pure context
  tax with little upside on a 32K box, and the SHA cache already
  prevents the unchanged case. The renderer can preserve
  `first_generated_at` and revision history in frontmatter without
  the executor ever seeing the old body.
- **Oversized-dir cap.** `Wiki:MaxDirBytes` is the static
  upstream gate (above). Stops a doomed run before it dispatches.
- **Coverage as an honesty channel.** When the executor *does*
  hit budget, `coverage.gaps` carries what it didn't reach. The
  rendered page surfaces the gap; the next nightly run sees the
  same SHA, skips, and the gap persists until adaptive splitting
  lands. Better than a page that lies.

## Drift command (preview, not v0)

`imp drift` runs after `wiki` and asks:

> Where do the wiki pages and `project/*.md` design docs disagree?

Implementation sketch (for confirming `wiki`'s artefacts feed it):

- Spawn one or more research runs in `fs` mode, scope = `wiki/` +
  `project/`. The brief asks the executor to enumerate
  contradictions between any wiki page's claims and any design
  doc's claims, populating `report.conflicts[]`.
- Render the report to `wiki/_drift.md` with each conflict as a
  section: claim, supporting wiki citations, contradicting design-doc
  citations, suggested resolution.
- The wiki's frontmatter already carries `source_tree_sha`, so
  drift entries can record the page version they were generated
  against — re-running drift after a `wiki` refresh resets stale
  entries automatically.

Nothing in v0 of `wiki` blocks this; the report and renderer shapes
already accommodate it. Adding drift later is additive.

## Sidecar archive

Per-invocation directory at `<parent>/<repo>.wikis/W-NNN-<slug>/`:

- `manifest.json` — plan + execution status per target.
- `<target-slug>/` per RUN target, containing the research-mode
  archive `<repo>.researches/R-NNN-...` linked or copied here so a
  single wiki run is self-contained.
- `meta.json` — `{started_at, completed_at, model, repo_root,
  wiki_dir, target, pages_written, pages_skipped_unchanged,
  pages_oversized_stub, pages_failed}`.
- `transcript.md` — concatenated per-target transcripts for human
  review.

Same pattern as `<repo>.researches/` and `<repo>.worktrees/`.
Greppable, ephemeral, gitignored.

## Tradeoffs / open calls

- **In-memory brief vs. brief file.** `Research.RunAsync` today
  takes either `--brief <path>` or free-text. The orchestrator
  needs to hand it a structured brief per target without writing
  N temp files. Cleanest fix: add an in-memory overload
  `Research.RunAsync(..., TaskDescriptor descriptor, ...)` that
  skips `BriefParser.ParseFile`. Small surface change in
  `Research.cs:32`.
- **`wiki` mode vs. survey-prompt-on-fs.** Registering a separate
  `wiki` mode is cleaner architecturally — modes are first-class.
  Alternative: keep `fs` and let the orchestrator pass a
  system-prompt override into `Research.RunAsync`. Modes route is
  recommended; keeps the prompt versioned with the mode and the
  audit story consistent.
- **Page placement: mirrored vs. co-located.** Recommended
  mirrored (`wiki/<path>.md`). Co-located is the ideal but
  conflicts with build-system file sweeps. A future symlink layer
  could surface mirrored pages co-located if a consumer wants it.
- **Wiki commit policy.** Default: commit. Configurable: gitignore
  if the consumer wants ephemeral. imp does not touch
  `.gitignore`; consumers manage it.
- **Renderer is pure (no model).** The renderer takes
  `report.json` to markdown deterministically. No second LLM pass
  to "polish" the page. Reasoning: model passes add cost,
  variability, and a place for model judgment to drift away from
  the cited evidence. The wiki must be auditable against the
  research run that produced it. If pages read robotically, fix
  the system prompt, not the renderer. (Repo-level synthesis in
  `wiki/README.md` is the deliberate exception, post-v0 — see
  Orchestrator model role.)
- **Light vs. heavy orchestrator.** v0 ships the orchestrator as a
  pure-C# loop; post-v0 ships it as a C# loop that calls the
  orchestrator model at specific decision points. The alternative —
  making the orchestrator itself a research-mode-style agentic loop
  with `dispatch_research` / `write_wiki_page` tools — is more
  flexible but harder to debug and budget-cap, and doesn't earn its
  complexity until multiple capabilities compete for the same loop.
  Light's decision-point seams are where heavy-mode tool boundaries
  would land if we ever cross over.
- **Index regeneration cost.** `wiki/README.md` is regenerated
  every run from frontmatter. For a thousand-page wiki this is a
  few hundred KB of disk reads — fine. If it ever isn't, cache
  the parsed frontmatter in the manifest.
- **Concurrent runs.** v0 is sequential — research mode is
  in-process and stdout-bound. Parallel dispatch is a future
  optimisation; on a single Strix Halo executor, serial is the
  natural shape anyway.
- **What counts as an in-scope directory?** v0: any directory
  with at least one allowlisted-extension file in it. Empty dirs
  and dirs of only ignored content do not get a page.
- **Cross-reference link resolution.** When a finding cites a
  path outside the current dir, the renderer links to the source
  file by default and additionally to the wiki page when one
  exists at render time. There's a chicken-and-egg edge — the
  first-ever wiki run won't have any wiki pages to link to.
  Acceptable: subsequent runs resolve cross-links correctly. Don't
  build a two-pass renderer for this.
- **Worktree dirty handling.** Same as research-mode policy:
  fall back to a content-hash walk and set `worktree_dirty: true`
  in frontmatter. The next clean-tree run regenerates with a real
  `source_tree_sha`.
- **`Wiki:MaxDirBytes` default of 40KB.** Tuned for 32K context
  with tool overhead, system prompt, and report output budgeted in.
  If the executor changes (larger context model, different
  prompt), revisit. Surface the threshold in `WikiResult` so a
  consumer can see how much was deferred to stubs.
- **Stub re-evaluation.** A stub page's `source_tree_sha` cache
  still applies — once a dir is below threshold (someone
  refactored it smaller), the next nightly run will see the SHA
  unchanged and skip the stub. Fix: stubs do *not* record a SHA in
  a way that would skip future runs. The frontmatter records the
  SHA-at-stub-time but the orchestrator treats `status: oversized`
  pages as always-stale until the stub-condition no longer holds.

## Build order

1. **`wiki` mode registration + orchestrator model role.** New
   `BuildWikiMode()` in `Modes.cs`, identical to `BuildFsMode()` but
   with `SystemPromptFileName = "research-fs-wiki.md"`. Ship the new
   prompt under `Prompts/`. Add `Wiki:Provider` / `Wiki:Model` /
   `Wiki:MaxDirBytes` / `Wiki:ToolBudget` / `Wiki:Dir` to
   `appsettings.example.json`. The orchestrator model isn't called
   by v0 code — config surface lands now so adaptive splitting,
   drift triage, and repo-level synthesis can wire to it without
   re-shaping config later. Verify mode registers; no behaviour
   change to existing fs mode.
2. **`Research.RunAsync` in-memory brief overload.** Adds a path
   that takes a `TaskDescriptor` directly. Existing file-and-text
   paths unchanged. Small refactor in `Research.cs:55-65`.
3. **Tree walker + cache logic.** `WikiPlanner` class: walks the
   target subtree, applies the ignore globs, computes
   per-directory `source_tree_sha` and source-bytes, builds the
   plan (SKIP / STUB / RUN). No dispatch yet — `imp wiki --dry-run`
   prints the plan and exits.
4. **Orchestrator + manifest.** `Wiki.RunAsync` consumes the plan,
   writes the manifest under `<repo>.wikis/W-NNN/`, dispatches
   RUN targets one at a time via `Research.RunAsync`, updates
   manifest entries. `--resume W-NNN` reads an existing manifest
   and skips done entries.
5. **Renderer.** `WikiPageRenderer`: pure function from
   `(ResearchReport, WikiPageContext)` to a markdown string with
   frontmatter. `WikiPageContext` carries source path, scope sha,
   timing, model name, status. Deterministic; covered by unit
   tests with golden files.
6. **Index regenerator.** `WikiIndexRenderer`: walks `wiki/`,
   parses frontmatter from each page, emits `wiki/README.md`.
7. **`imp wiki` CLI handler.** Wires steps 3-6 together. Flags:
   `[path]`, `--full`, `--dry-run`, `--resume <run-id>`. Stdout
   emits `WikiResult`.
8. **Validation pass.** Run on imp itself. Inspect a half-dozen
   pages by hand; refine the system prompt and renderer. Run
   `imp wiki` a second time — confirm SHA cache hits across the
   board (zero RUN dispatches when nothing changed).
9. *(later)* `Wiki:Ignore` glob list and allowlist surface in
   config — start with sensible hardcoded defaults and only add
   config knobs when a real consumer needs them.
10. *(later)* `imp drift`. New command that runs research over
    `wiki/` + `project/*.md`, looking for conflicts. Renders
    `wiki/_drift.md`, with the orchestrator model grouping the
    flat `Conflict[]` into actionable buckets.
11. *(later)* Adaptive splitting. When a dir is oversized, the
    orchestrator model is handed the file manifest and proposes a
    clustering (≤`MaxDirBytes` per chunk, related files kept
    together). The orchestrator dispatches one research run per
    cluster and stitches the resulting pages. Replaces the v0 stub.
11a. *(later)* Repo-level synthesis. `wiki/README.md` rendered by
    the orchestrator model rather than the deterministic tree
    walker — produces a real overview of how the codebase is
    organized. First model-driven render in the pipeline.
11b. *(later)* Per-target brief tailoring. The orchestrator model
    writes per-dir briefs shaped by parent/sibling context instead
    of the v0 template.
12. *(later)* Parallel dispatch across targets, gated by executor
    capacity.
13. *(later)* `imp wiki --check-drift` — convenience flag that
    chains `wiki` + `drift` in one invocation.
