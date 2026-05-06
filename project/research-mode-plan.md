# imp research mode — feature plan

> Status: design sketch (2026-05-04). Not implemented. Needs review,
> then a slimmed phase-1 to land FS mode end-to-end before web mode
> or the pluggability surface ships.

A second top-level command alongside `imp build`. Hands a question
(or a structured brief) to a cheap executor running with a curated
toolset and sandbox profile, and returns a citation-anchored
structured report optimized for parent-AI consumption.

Motivating prior art: the open design question at
`memory/project_contract_authoring_cost.md` flagged that Sonnet
over-reads when authoring contracts. A research mode is the other
half of that fix — give the parent a cheap way to delegate the
research grind, not just the implementation grind.

### Where this design sits in the field

Three live citation conventions in shipping research APIs as of
May 2026, with no convergence:

- **Inline span annotations** — OpenAI Deep Research. Citations are
  character offsets into prose. Optimized for human-readable markdown.
- **Two-pass with sidecar citations** — Anthropic. Lead writes prose
  without citations; a `CitationAgent` post-pass attributes claims.
- **Field-level basis** — Parallel, Exa, Tavily. No prose; citations
  attach to caller-defined schema fields, with per-field
  `{citations[], reasoning, confidence}`.

This design picks **field-basis**. The consumer is an agent that will
walk the structure programmatically, not render markdown. Inline
spans force span-math on the consumer; two-pass leaves prose between
the consumer and the evidence. Field-basis is the AI-consumer-native
lineage — that is the bet, stated explicitly so future-us knows why
inline annotations were refused.

The novel piece relative to the field: every shipping research API
assumes web. **fs-mode for code-research with structured file
provenance** (path:line + git SHA) isn't covered by Parallel / Exa /
Tavily / OpenAI / Anthropic. Free provenance — we're sitting in a
worktree at a known commit — is the moat.

## Goals

- Two built-in modes: **web** (network-only) and **fs**
  (filesystem-only). Each mode's defining feature is a hard guarantee
  about what the executor *cannot* reach.
- A pluggable mechanism so consumers can ship their own modes —
  motivating examples: OLAP-cube researcher, Lucene-doc researcher,
  graph-DB researcher. Each is "imp's executor loop + a curated set
  of tools that hit a specific data backend."
- Reports optimized for AI consumers: citation-anchored, dense, no
  narrative prose, same envelope across all modes.

## Non-goals

- Not replacing `imp build`. Build mutates state in a worktree;
  research is non-mutating and runs against the main checkout (or
  no filesystem at all). Forcing one command to model both makes
  both worse.
- Not a planning tool. Research returns findings; the parent
  decides what to do with them.
- Not multi-step orchestration. One question per invocation. If the
  parent wants follow-ups, it issues another `imp research`.

## Why now

Two reuse signals from the existing codebase make this cheap:

- `Tools.CreateReadOnly()` (`Tools.cs:224`) already enumerates the
  exact toolset FS-mode wants: read_file, grep, list_dir.
- The closeout sub-loop in `Executor.RunCloseoutAsync`
  (`Executor.cs:511`) already runs a different toolset, system
  prompt, and structured-finish tool inside the same executor
  scaffolding. The pattern is proven; research mode is a
  generalization.

Sandbox is already in place too: `SandboxConfig` toggles network and
docker mode. The new axis is mount policy (rw / ro / none).

## Top-level shape

```
imp research --mode=web "question text" [--brief contracts/R-NNN.md] [--schema schema.json]
imp research --mode=fs  "question text" [--brief contracts/R-NNN.md] [--schema schema.json]
imp research --mode=<custom> ...
```

- Free-text question is the common case. `--brief` points at a
  structured brief file (analogue of a build contract; see §6).
- `--schema schema.json` (optional) lets the caller override the
  default report shape with their own JSON Schema. Tavily / Exa /
  Parallel all expose this as the dominant AI-consumer pattern; the
  caller knows what shape it wants, the agent fills it in with
  per-field basis. Each mode ships a default schema (the
  `report.json` shape below); `--schema` overrides it.
- Stdout: a single `report.json`. Sidecar artefacts in
  `<parent>/<repo>.researches/R-NNN-<slug>/`:
  `brief.md` (verbatim input), `report.json`, `findings.jsonl`
  (one finding per line for cheap grepping), `transcript.md`,
  `trace.jsonl`, and `meta.json` (`{created_at, brief_hash,
  model, tags, sources_count}`). The dir parallels the build
  side's `<repo>.worktrees/T-NNN.trace/` and is intentionally
  greppable — the parent does its own retrieval over this archive
  rather than imp shipping a prior-report lookup primitive (see
  Tradeoffs / open calls).
- Long-running, like `build`. Minutes, not seconds.

## Core abstraction: tools as the primary pluggability axis

A **mode** is a (toolset, sandbox-profile, system-prompt,
finish-schema) tuple. Of those four, the toolset is the most
customized — that's where consumers express "this mode talks to my
OLAP cube" or "this mode reads our Lucene index." Sandbox profile,
prompt, and finish schema are usually derived from the toolset's
capability footprint.

Treat the **tool registry** as the central feature; modes are
recipes that compose registered tools.

```csharp
// Each tool is registered once with metadata that lets a mode
// declare requirements without knowing implementation details.
public interface IToolDefinition {
    string Name { get; }                  // "http_get", "lucene_query", "graph_traverse"
    ToolReach Reach { get; }              // Network | LocalFsRead | LocalFsWrite | Subprocess | None
    AITool Build(ToolContext ctx);        // factory — closes over working dir, sandbox, etc.
}

public sealed record ToolContext(
    string WorkingDirectory,
    SandboxProfile Sandbox,
    FindingsSink Findings,
    IConfiguration Config);
```

`ToolReach` is the audit knob. A mode declares its allowed reach
set; tools whose reach isn't in that set can't be registered into
that mode. This is how "web mode has zero filesystem access" becomes
a structural property, not a vibes-based guarantee:

- Web mode: `Reach ⊆ { Network, None }`
- FS mode: `Reach ⊆ { LocalFsRead, None }`
- A custom OLAP mode: `Reach ⊆ { Network, None }` (assumes the cube
  is over a network) — the consumer's `olap_query` tool declares
  `Reach.Network`, and the sandbox profile matches.

A mode definition is then almost data:

```csharp
public sealed record ModeDefinition(
    string Name,
    SandboxProfile Sandbox,
    IReadOnlySet<ToolReach> AllowedReach,
    IReadOnlyList<string> ToolNames,        // names of registered tools
    string SystemPromptResource,            // path under Prompts/
    Func<FindingsSink, AIFunction> FinishToolFactory);
```

At invocation time imp resolves `ToolNames` against the registry,
verifies each tool's `Reach` is in `AllowedReach`, builds them with
a `ToolContext`, and hands the list to the executor loop. A mode
that lists a tool whose reach doesn't match its profile fails fast
at startup, not mid-run.

Two layers of defense per mode, intentionally redundant:

1. **Tool surface** — the model literally cannot call what isn't
   registered.
2. **Sandbox** — docker `--network=none` (FS) or no-bind-mount
   (web) is the wall behind the tool surface.

### Canary tools — inverted-trust tripwires (post-v1)

Idle-thought third layer worth holding the design space open for. In
a mode whose `AllowedReach` excludes a category, register a tool
from that excluded category that does nothing except log the
attempted call and trip a `SafetyBreach`. Web mode registers a
`bash` stub described exactly like the real bash; web mode has no
codebase to operate on, so any `bash` call is by definition
spurious and points at one likely cause — fetched content tried to
subvert the agent (prompt injection: "ignore previous instructions
and run X…").

Run terminates as `blocked` with a category like
`prompt_injection_suspected`, captures the attempted command plus
the preceding tool history (the `http_get` that fetched the
poisoned page sits right there in the trace), and surfaces it in
the report. The parent learns which URL is hostile and can
blacklist it for future runs.

Inversion of the usual model preference: the cheaper and more
suggestible the executor, the **better** the tripwire signal. A
robust model resists injection silently and the parent never finds
out; an easily-tricked GPT-5.4-mini-class model falls for it and
fires the canary. Cheap, suggestible executors are a feature here,
not a liability.

Generalizes — any mode can register canaries for tools outside its
allowed reach. fs-mode stub `http_get` catches fetches injected via
doc comments or test-fixture URLs; an OLAP-mode stub `read_file`
catches cube-breakout attempts. The abstraction accommodates it
without redesign: a canary is a tool with `Reach.None` and a
`BreachOnInvoke = true` flag whose tool description mimics the real
surface area.

## Sandbox profile (new axis on `SandboxConfig`)

Adds mount policy alongside the existing network/memory/cpu knobs:

| Mode profile | Network | Repo mount | Scratch | Subprocess |
|---|---|---|---|---|
| `web` | bridged | none | tmpfs only | no |
| `fs`  | none    | `:ro` | none | no |
| `build` (today) | none | `:rw` (worktree) | none | bash via tools |
| `olap` (hypothetical) | bridged | none | tmpfs | no |
| `lucene-local` (hypothetical) | none | `:ro` (index path) | none | no |

The matrix shows why "tools as the axis" is the right factoring:
you can't author one of these without picking the toolset, and once
the toolset is picked the sandbox row is largely determined.

## Web mode (built-in)

The research loop is `web_search → http_get → extract_text → repeat`.
Search is the discovery primitive; `http_get` is still how you cite,
because search-result snippets are too thin for the citation
contract (no `content_hash`, no full passage, no trustworthy
`retrieved_at`).

Toolset (small; resist creep):

- `web_search(query, max_results)` →
  `{results: [{url, title, snippet, score}], answer?: string}`.
  The optional `answer` field is filled when the provider supplies
  one; the executor's prompt is told it's a hint, never a citation.
- `http_get(url, max_bytes)` → `{status, headers, body, content_type, fetched_at, content_hash}`.
  Per-call body cap (default 256KB) and per-run total-bytes cap.
- `extract_text(html)` — pure function, html → readable text. Keeps
  fetched bodies small in context.

Per-run search budget (`max_searches`) is a sandbox-profile knob —
search APIs charge per call, and a runaway loop is more expensive
than runaway `http_get`. Default 20.

No `bash`, no `read_file`, no `write_file`. Sandbox: docker bridged
network, no repo mount, ephemeral `/tmp` only.

### Search provider abstraction

`web_search` resolves through `ISearchProvider`, mirroring the
existing `Providers.cs` pattern for LLM providers (compile-time
switch over named providers, configured in `appsettings.json`, no
runtime plugin loader). Same convention, same testability.

Default: **Tavily**. AI-agent-native — its response shape carries
LLM-cleaned `content` text and an optional synthesized `answer`,
both directly usable by the executor without secondary parsing.
Free tier covers light use; pay-as-you-go beyond.

Live alternative: **Exa**. Kept as a first-class supported
implementation, not a future-extension footnote. Reason: if Tavily's
monthly bill gets out of control, Exa's neural/semantic search is
the obvious swap — same agent-native ergonomics, different pricing
curve, better fit for "papers/posts about concept X" queries that
keyword search handles poorly. Switch is a one-line config change:
`"SearchProvider": "Exa"` in `appsettings.json`.

```json
// appsettings.example.json
"Search": {
  "Provider": "Tavily",     // or "Exa"
  "Tavily": { "ApiKey": "...", "MaxResults": 5 },
  "Exa":    { "ApiKey": "...", "MaxResults": 5, "Mode": "neural" }
}
```

Brave Search is fine to add later if a consumer wants raw
keyword-style results without LLM-shaping; not worth implementing
upfront. The `ISearchProvider` interface stays small: one method,
`Task<SearchResult> SearchAsync(string query, int maxResults, CancellationToken ct)`.

## FS mode (built-in)

Toolset = exactly `Tools.CreateReadOnly()` — `read_file`, `grep`,
`list_dir`. Reuse, don't fork.

Operates against the **main checkout, read-only**. Research is
non-mutating; the worktree machinery is overhead. Mount cwd at
`/work:ro` in docker mode. In host mode, refuse to register write
tools and trust that's enough.

Sandbox: docker `--network=none`, repo `:ro`. No bash. The model
can't shell out, can't curl, can't write.

## Future modes (motivating examples for the abstraction)

These don't ship in v1 but the abstraction has to make them buildable
without forking imp. Each is a worked example of "register a tool,
declare a mode."

- **OLAP-cube researcher.** Tool: `cube_query(mdx)` with `Reach.Network`.
  Mode: bridged network, no repo mount. Useful for delegating
  exploratory analytical queries without the parent burning Opus
  tokens reformulating MDX.
- **Lucene-doc researcher.** Tool: `lucene_search(query, index_path)` with
  `Reach.LocalFsRead` (assuming a local index) or `Reach.Network`
  (Solr/Elastic). Mode profile follows the reach. Lets the parent
  delegate "find all docs matching this faceted query" without
  paying Sonnet rates to read 200 result snippets.
- **Graph researcher.** Tool: `graph_traverse(start, depth, predicate)` with
  `Reach.Network` (Neptune / Neo4j). Mode: bridged network, no repo.
- **LSP researcher.** Tool: `lsp_find_references(symbol)` with
  `Reach.Subprocess` (runs a language server). Sandbox: same as fs
  but with a child-process tool whose subprocess footprint is
  declared up front. (`project/lsp-integration-research.md` already
  exists — that work composes cleanly with this abstraction.)

The shape is the same every time: register tools with reach
metadata, declare a mode that lists them, ship a system prompt that
teaches the executor how to use them. No imp-side code changes once
the tool registry and mode registry exist.

## Pluggability surface

Three options, in increasing complexity:

- **(a) Source-level registry.** Modes and tools registered in
  `Modes.cs` / `Tools.Registry.cs`. Consumers fork or PR.
- **(b) Assembly-discovery plugins.** Scan a `plugins/` dir for
  assemblies implementing `IToolDefinition` / `ModeDefinition`.
  Adds a load context — explicitly removed once before
  (`Providers.cs:13`).
- **(c) Config-driven manifests.** JSON describes a mode (tool
  names + sandbox profile + prompt path + finish schema). No code.

**Recommendation: (a) for v1.** Document the registry contract
clearly so a consumer can vendor imp and add a tool/mode in their
fork in under 50 lines. Revisit (b) only if real demand surfaces;
revisit (c) only if multiple consumers want to compose modes
without compiling — at which point the manifest format is an
inevitable second project.

The decision aligns with the existing "no DI scaffolding unless a
library expects it" stance and matches how `Providers.cs` handles
its own pluggability (compile-time switch over named providers, no
runtime plugin loader).

## Report shape (the AI-optimized output)

Same envelope across all modes. Dense, parser-friendly, no
narrative ceremony. Every shipping AI-consumer research API
(Parallel, Exa, Tavily) converges on three traits: caller-supplied
schema, mandatory excerpts on every citation, and per-field basis
attached to structured outputs. The shape below matches that
convergence.

```json
{
  "mode": "fs",
  "question": "Where does imp validate scope-file existence?",
  "started_at": "...", "completed_at": "...",

  "usage": {
    "tool_call_count": 9,
    "tokens_in": 4231,
    "tokens_out": 612,
    "wall_seconds": 47,
    "estimated_cost_usd": 0.018
  },

  "synthesis": "Single paragraph. Direct answer. No 'I found that...' framing.",

  "coverage": {
    "explored": ["Contract.cs", "Executor.cs"],
    "not_explored": ["docs/*"],
    "gaps": ["didn't check tests/"]
  },

  "findings": [
    {
      "claim": "Scope-file existence is enforced in ContractValidator.Validate",
      "citations": [
        {
          "kind": "file",
          "path": "Contract.cs",
          "line_start": 154,
          "line_end": 170,
          "excerpts": ["if (!File.Exists(full)) throw new ContractException(...)"],
          "git_sha": "a06c4d3..."
        }
      ],
      "confidence": "high",
      "reasoning": "Direct existence check at the validator entry point; no other call site bypasses it."
    },
    {
      "claim": "An OLAP cube fact ...",
      "citations": [
        {
          "kind": "url",
          "url": "https://example.com/...",
          "retrieved_at": "2026-05-04T14:22:11Z",
          "content_hash": "sha256:...",
          "excerpts": ["..."]
        }
      ],
      "confidence": "medium",
      "reasoning": "Single source; no corroborating reference found."
    }
  ],

  "conflicts": [
    {
      "claim": "Whether scope enforcement is pre-flight or post-hoc",
      "supporting_findings": [0],
      "contradicting_findings": [],
      "resolution": "Pre-flight, per Contract.cs:154. The README claim of post-hoc enforcement is stale.",
      "reasoning": "Code is the ground truth; doc was last updated before the validator landed."
    }
  ],

  "follow_ups": [
    "Open question about how delete: handles missing files — outside this brief"
  ],

  "blocked_questions": [
    {
      "question": "Does the build path also enforce scope, or only validation?",
      "assumed_instead": "Treated as out-of-scope for this brief; only the validator path was audited."
    }
  ]
}
```

Key choices, all aimed at AI consumers:

- **`synthesis` first, `findings[]` second.** A parent that just
  needs the answer reads `synthesis`. A parent that needs to verify
  or chain follow-ups reads `findings[]`. No prose summary, no
  markdown rendering — that's `transcript.md`'s job.
- **Per-finding categorical confidence (high / medium / low),
  defined in the executor's system prompt.** No report-level
  confidence — a single bucket for a multi-claim report hides too
  much. Categorical, not numeric: numeric confidence from LLMs is
  poorly calibrated and gives false precision (BAML, HN
  consensus). Parallel and Anthropic both ended up here.
- **Citations are mandatory and carry excerpts.** The finish tool
  rejects findings without at least one citation, and rejects
  citations without at least one excerpt. Excerpts make findings
  auditable without re-fetching, which is the whole point of
  field-basis. Same enforcement pattern as the existing closeout
  reviewer (`Executor.cs:534`).
- **Per-finding `reasoning`.** One sentence on why the citation
  supports the claim. Matches Parallel's `FieldBasis.reasoning`.
  Saves the parent from inferring the link.
- **fs-mode citation contract is structured and provenance-rich:**
  `{kind: "file", path, line_start, line_end, excerpts[], git_sha}`.
  The git SHA is free — we're in a worktree at a known commit — and
  is the structural equivalent of `retrieved_at + content_hash` for
  web sources. Web research APIs can't give you this; we can.
- **`conflicts[]` is first-class.** When sources disagree, surface
  the conflict — supporting findings, contradicting findings, the
  resolution chosen and why. The shipping field has no equivalent
  (Anthropic surfaces conflict in subagent prose; Parallel / Exa /
  Tavily don't expose it). For a code-research consumer, an
  unflagged contradiction is worse than a low-confidence answer.
- **Citation kinds are extensible.** `file` and `url` ship with
  required field sets enforced by the finish tool. Modes can
  register new kinds (`cube_cell`, `lucene_doc_id`,
  `graph_node_id`) by declaring their required-field schema at
  registration time.
- **`coverage` is explicit.** What got looked at, what didn't, where
  gaps remain. Lets the parent decide whether to issue a follow-up.
  No shipping research API exposes this — keep it.
- **`blocked_questions[]` with assumed-instead.** No mid-flight
  clarification (matches Anthropic: "No clarifications will be
  given — use best judgment"). Burden falls on the agent to flag
  what it would have asked *and the assumption it made instead*.
  The parent re-dispatches with the assumption corrected if needed.
- **`usage` block is required, not optional.** Tool calls, tokens,
  wall-clock, cost estimate. Exa exposes this on every response;
  we should too. Lets the parent build a self-tuning calling
  pattern over time.
- **No human-friendly fields.** No prose recap, no
  recommendations, no markdown. The transcript is the human path.

## Subagent task descriptor

Even with v1 being single-executor, the brief handed to the
executor is structured. Lifted from Anthropic's lead-agent prompt
(field-tested across their multi-agent research system); forces
brief-clarity and gives a clean upgrade path to multi-executor
without redesigning the contract.

```
- objective: one specific question this run answers
- expected_output: which fields of the report shape must be filled
- background: prior context the executor needs (often: the parent's
  current task, what's already been researched)
- key_questions: sub-questions the synthesis should resolve
- suggested_sources: starting points (file paths for fs, seed URLs
  for web)
- scope_boundaries: what's out of scope (forbidden paths, excluded
  domains)
```

When invoked with free-text, imp synthesizes a descriptor whose
only filled field is `objective`. When invoked with `--brief`, the
brief parses directly into this struct.

## Brief format (when not using a free-text question)

Lighter than a build contract.

```markdown
## R-007: Audit how scope-adherence is enforced

**Question:** What enforces "no changes to files outside Scope" today?

**Sub-questions:**
- Is enforcement pre-flight or post-hoc?
- Does closeout get the final say?

**Sources:** Contract.cs, Executor.cs, contracts/T-*.md
**Forbidden:** docs/ (out of date)
**Output:** A decision-grade answer for whether to add a pre-flight gate.
```

Validates the same way contracts do (lenient parse; fail loud on
missing **Question:**). Free-text mode synthesizes a brief whose
only field is Question.

## Tradeoffs / open calls

- **Worktree for FS research?** Proposing no — read-only against
  the main checkout. Trade: a pending edit in cwd is visible to the
  researcher, which can be a feature (research-the-WIP) or a
  footgun (researcher anchors on uncommitted noise). Lean
  main-checkout, document the gotcha. Note: the `git_sha` on every
  fs-mode citation collapses to "HEAD + dirty flag" when there are
  uncommitted changes; the report should expose the dirty flag so
  the parent knows the citation is against in-flight code.
- **Search provider default.** Tavily ships as the default;
  Exa is a first-class swap target if Tavily's bill grows
  unwieldy. Brave / SerpAPI are footnotes — add only on real
  consumer demand. Optional seed URLs in the brief are still
  supported when the parent already knows good starting points,
  but they're an optimization, not a v1 requirement.
- **One docker image or two?** Web wants curl/wget; FS wants
  nothing. One image with tool-surface gating is simpler; two
  images is defense-in-depth. v1: one image, gated by toolset.
- **Closeout-style verification pass?** A second mode-pass that
  re-reads the report and tries to falsify findings would be
  valuable but doubles cost. Anthropic does this implicitly via
  their `CitationAgent` post-pass; the survey work (DRAGged,
  FaithfulRAG) treats falsification as a core unsolved problem.
  Post-v1, gated by `--verify`.
- **Where do consumer plugins register tools/modes?** v1 answer:
  edit `Modes.cs` / `Tools.Registry.cs` in a fork. If multiple
  consumers want this, revisit assembly-discovery (b).
- **Tool registration for built-ins.** Should imp's *own* core
  tools (read_file, grep, list_dir, http_get) flow through the
  same registry, or stay as direct constructors used by the
  built-in modes? Keeping built-ins in the registry means modes
  are uniformly composable; keeping them outside means the
  registry stays small and consumers don't accidentally
  shadow/override core tools. Lean: built-ins go through the
  registry but are marked `IsBuiltin = true` and can't be
  unregistered.
- **Budget caps in the request.** Exa surfaces actual usage; the
  next step is letting the caller cap up front via
  `--max-tool-calls`, `--max-tokens`, `--max-wall-seconds`. v1:
  expose actual usage in the report (already in the shape above);
  defer caller-side caps until we see runaway runs in practice.
- **Source credibility scoring.** Academic systems build whole
  classifier agents for this; shipping APIs roll it into
  per-finding confidence. We're following the shipping convention.
  Future extension lives on the `ToolReach` plugin axis (a
  "credibility-scorer" tool a mode can compose in).
- **Result caching / research memory.** Decided: **no prior-report
  lookup in v1.** No shipping research API (OpenAI DR, Anthropic,
  Parallel, Exa, Tavily, LangGraph) ships a "have you researched
  this before?" primitive — every system punts retrieval to the
  parent harness. The parent (Claude Code) is also better
  positioned: it has skills, memory, and cross-session intent that
  `imp` (one-shot CLI per invocation) cannot. Result staleness is
  also unsolved in the literature — Agentic Plan Caching
  (arXiv 2506.14852) deliberately caches *plans* not *results* for
  this reason. Ship the **write side** instead: emit reports into
  a predictable, greppable archive (see Sidecar artefacts above)
  with `meta.json` carrying `{created_at, brief_hash, tags,
  sources_count}`. The parent retrieves its way — grep,
  embed-on-demand, `find -newer`, manual scan. Upgrade path: if
  usage demands it, add `imp research --check-prior <brief>` later
  that does lexical + embedding similarity over prior briefs and
  prints top-k matches with ages — strictly **advisory**, never
  auto-skip a research task. If supersession is ever needed,
  Zep's bitemporal `{valid_from, valid_to, invalidated_by}` model
  is the one to copy.

## Build order

1. **Tool registry + mode abstraction.** Define `IToolDefinition`,
   `ToolReach`, `SandboxProfile`, `ModeDefinition`,
   `TaskDescriptor`. No behavior change yet — existing build code
   keeps using `Tools.Create` directly. The registry is additive
   scaffolding.
2. **FS mode end-to-end.** Reuses `CreateReadOnly` (rebadged as
   registry entries), runs against main checkout, finish tool
   emits the report shape above with mandatory citations + excerpts
   + per-finding categorical confidence + `conflicts[]` +
   `blocked_questions[]` + `usage` block. Citation kind `file`
   carries `{path, line_start, line_end, excerpts[], git_sha}`
   (with dirty flag when the worktree has uncommitted changes).
   Lowest risk because every part exists.
3. **Archive + report renderer.** `<repo>.researches/R-NNN-<slug>/`
   layout: `brief.md`, `report.json`, `findings.jsonl`,
   `transcript.md`, `trace.jsonl`, `meta.json`. Reuse
   `TraceWriter` and `TranscriptRenderer`. The greppable archive
   is the answer to "have you researched this before?" — parent
   does retrieval, imp does write.
4. **`imp research` CLI subcommand + brief parser.** Free-text
   first, then `--brief`. Defer `--schema` until after phase 5
   validation — see what real callers actually want before
   building the schema-override path.
5. **Validate phases 1–4 on real questions** before building web
   mode or `--schema`. FS mode alone is already a Sonnet-token
   saver and a good test of whether the report shape is consumable.
6. **Web mode.** New `web_search` / `http_get` / `extract_text`
   tools, sandbox profile with bridged network and no mount.
   Citation kind `url` carries
   `{url, retrieved_at, content_hash, excerpts[]}`. `web_search`
   resolves through `ISearchProvider` (Tavily default, Exa as
   first-class swap). Per-run `max_searches` budget enforced at
   the sandbox-profile level. Search-result snippets are
   explicitly not citations — the executor must `http_get` and
   excerpt before claiming a finding.
7. **`--schema` flag.** Caller-supplied JSON Schema overrides the
   default report shape; per-field basis attaches to caller-defined
   fields. Tavily / Exa / Parallel pattern. Gate on phase 5
   feedback — only build if there's a concrete caller need.
8. **Pluggability docs.** `docs/modes.md` showing how to register
   a third mode in a fork, using OLAP/Lucene/graph as worked
   examples.
9. *(later)* `--verify` flag for closeout-style finding
   falsification (Anthropic's `CitationAgent` post-pass pattern).
10. *(later)* `imp research --check-prior <brief>` — advisory
    similarity lookup over the archive, never auto-skip. Gate on
    seeing the parent actually want this in practice.
11. *(later, if asked)* Assembly-discovery plugin loading.
12. *(later, if asked)* Caller-side budget caps
    (`--max-tool-calls`, `--max-tokens`, `--max-wall-seconds`).
13. *(later)* Canary tools as a third defense layer. Web-mode `bash`
    stub first (highest-signal, lowest cost); generalize to other
    modes once the captured-breach forensic shape stabilizes.
