# Plan to add Ollama support

## Premise

Someone with a 24GB GPU wants to run the executor locally instead of
paying Azure/OpenAI/Anthropic per token. The budget-cap motivation
that shaped this project applies doubly to people without an Azure
allowance — free-at-the-margin local inference is the natural
endpoint of "cheap slow model grinds through contracts."

This is a provider-add, not a capability gate. Local inference
either produces runs that close out cleanly or it doesn't; the
surrounding harness doesn't change. The question this plan is
mostly trying to answer: **do current open-weight models on a 4090
actually clear the bar this harness already sets for a provider?**

## Feasibility — honest take

The harness is demanding in ways that matter for local models:

- **Tool-calling reliability is load-bearing.** `Executor.cs:52`
  runs up to 500 tool calls with strict JSON schemas. `finish_work`
  uses `ToolMode.RequireAny` and expects a nested `reports[]`
  structure. Closeout does the same in fresh context. A model that
  emits malformed tool calls 10% of the time will burn contracts
  faster than it finishes them.
- **16K output tokens per turn.** Reasoning models need headroom
  for hidden CoT + visible tool-call payloads. Local models with
  native reasoning (gpt-oss, Qwen3-thinking variants) are the
  natural fit; non-reasoning instruct models will emit visible
  reasoning into the user-facing text, which works but bloats
  history fast.
- **Context bloats through the loop.** A scope-narrow contract
  still accumulates tool results. Realistic working context at
  turn 50 is 20–40K tokens. Ollama's default `num_ctx` is 4096 —
  anything lower than ~32K in config will truncate silently and
  destroy the loop.
- **Same model for executor + closeout.** If both are a weak
  local model, they can share blind spots. Codex-pattern
  reviewer-catches-lying-executor (phase 5 of v2-plan) weakens
  when reviewer has the same limitations.

### What fits on a 4090 (24GB VRAM, ~21.5GB usable)

The dev box has 24564 MiB total but the desktop session holds
~2.4 GB permanently (X server, compositor, browser). Realistic
usable budget for Ollama + KV cache is **~21.5 GB**, not 24.
That puts some models on the table in theory but not in practice
unless the desktop is logged out.

Ranked by how well they match what this harness actually asks for:

| Model | Quant | VRAM @ 32K ctx | Headroom on 21.5GB | Why it's a candidate |
|---|---|---|---|---|
| **Devstral-Small-2507 (24B)** | Q4_K_M | ~16–18GB | ~4–5GB, comfortable | Mistral's agentic coder, trained on SWE-Bench-style tool loops. Closest thing to "purpose-built for this harness" |
| **gpt-oss-20b** | Q4 | ~13–15GB | ~7–8GB, roomy | Native reasoning traces, native tool calling; OpenAI-released, Ollama-blessed. Headroom to push ctx past 32K |
| Qwen3-Coder-30B-A3B | Q4_K_M | ~20–22GB | 0–2GB, tight | MoE, fast decode, strong agentic tuning — but only workable at 16K ctx, or with the desktop logged out. Viable on someone else's 4090 with no display load |
| Qwen2.5-Coder-32B | Q4_K_M | ~22GB | negative | Doesn't fit with a desktop session live. Older generation anyway |

Not viable on this box: Llama 3.3 70B, DeepSeek-V3, anything
below ~14B (tool-calling reliability falls off).

**Starting pick: Devstral-Small, local testing on this machine.**
Only model on the list whose training objective directly matches
what this harness runs (multi-turn tool-use on code) AND whose
VRAM footprint leaves real headroom on the live dev box. Fallback
to gpt-oss-20b if Devstral's tool-call format plays badly with
our schemas.

**Ship recommendation for users with more VRAM:** document
Qwen3-Coder-30B as the preferred model for anyone running
headless, on a second GPU, or with >24GB. "Will probably work
better on your machine" is an honest pitch — the 4090-with-
desktop constraint is ours, not theirs.

### Throughput reality

At Q4_K_M with 32K context on a 4090:
- 24–30B dense: ~30–50 tok/s decode
- MoE 30B-A3B: ~80–120 tok/s decode

A 50-turn contract with ~8K output tokens per turn (typical
reasoning + tool-call payload) is **20–60 minutes of wall time
per contract** on dense, 10–25 minutes on MoE. Azure's GPT-54
at comparable contract size runs 5–15 minutes. Local is 2–4×
slower. Not a blocker — the whole premise is "cheap slow model"
— but worth calibrating expectations.

### Integration effort

Low. Ollama exposes an OpenAI-compatible API at
`http://localhost:11434/v1`. The existing `CreateOpenAI` in
`Providers.cs:64` would work verbatim if we let the endpoint
be overridden. A dedicated `Ollama` case in the switch keeps it
legible and makes pricing/prompt routing easier.

## The path

| Phase | Goal | Ships | How you validate | Sessions |
|---|---|---|---|---|
| **1. Provider wire-up** | Harness can target a local Ollama and finish a trivial contract | `Ollama` case in `Providers.cs` (OpenAI-compatible client with configurable `BaseUrl` + `Model` + `NumCtx`); example config block; `Pricing.cs` returns $0 for Ollama models; `dotnet run -- --ping Ollama` works | T-001 (trivial write_file contract) runs end-to-end with Devstral-Small and terminates `success`. Trace is readable, POW is well-formed | 1 |
| **2. Tool-call shakeout** | Model emits our schemas reliably enough to trust | Author `Prompts/Ollama.md` if default prompt doesn't produce clean `finish_work` calls (likely needed); tighten schemas if the model struggles with nested `reports[]`; log tool-call parse failures in trace if not already | Run T-002 through T-005 (existing validation set). Measure: fraction of runs that hit `finish_work` cleanly vs. bail with finish_reason=length / malformed call. Write findings to project/ollama-notes.md | 1–2 |
| **3. Real contract** | Enough signal to say "works" or "doesn't" | Pick 2 medium contracts from the v2 backlog, run each 3× on local. Capture terminal-state distribution, tool-call count, wall time, closeout verdict | Compare against same contracts run on Azure. Decision point: is this good enough that a 4090 owner should use it? Answer honestly — "not yet, but close" is a valid outcome that ends the plan here | 1 |
| **4. (Conditional) Mixed-mode closeout** | Local executor, cloud reviewer — cheap execution, trustworthy verdict | Add config knob so closeout uses a different provider than the executor; reviewer routes to Anthropic/Azure while executor stays on Ollama | Same closeout "lying contract" from v2 phase 5 gets caught even when executor is a weak local model. Ship only if phase 3 shows local closeout has a meaningful false-pass rate | 1 |

**2–5 sessions total, depending on where the honesty check
lands.** Phase 4 is the one that earns its keep if phase 3 shows
local models are decent executors but unreliable reviewers.

## Why this plan is short

Three reasons:

1. The provider abstraction already exists and is clean. Switch
   statement in `Providers.cs`, per-provider prompts in
   `Prompts/`, pricing table in `Pricing.cs`. All the extension
   points this needs are already there.
2. The interesting work is empirical, not architectural. "Does
   Devstral close out a real contract" isn't answerable from a
   design doc — it's answerable by running it.
3. If current open-weight tool-calling isn't good enough, there's
   nothing to build. Better to find out in one session than ten.

## Honesty check

Phase 1 → phase 2 → **stop and read traces**. If the model is
routinely producing malformed tool calls, emitting prose where a
function call belongs, or failing `finish_work` with the wrong
schema, the bottleneck is model capability, not our integration.
In that case: ship phase 1 + phase 2 as "Ollama provider exists,
quality is model-dependent, here's what we observed," document
the state, and defer phases 3–4 until open-weight tool-use
reliability catches up (probably one model generation away).

The worst outcome is shipping a polished Ollama integration that
rarely closes out real contracts. Better to ship a blunt one with
honest notes.

## Explicitly deferred

Not on the path unless real signal demands them:

- **llama.cpp / vLLM / MLC direct integration.** Ollama's
  OpenAI-compatible endpoint gets us to usable fastest; other
  runtimes are optimizations. Swap later if throughput becomes
  the bottleneck (it probably won't — model capability is).
- **Multi-GPU / 70B models.** Out of 4090 scope. If someone
  shows up with 2×4090 or an H100, that's a different plan.
- **Streaming / partial tool-call parsing.** The existing
  `IChatClient` code path accumulates the full response per
  turn; local models can stream but we don't need it to work
  correctly. Latency cosmetic, not capability.
- **Structured-output / grammar-constrained decoding.** Ollama
  supports JSON mode, but `Microsoft.Extensions.AI` surfacing is
  not obvious and our tool-call path already enforces structure.
  Revisit only if phase 2 shows malformed tool calls are the
  dominant failure mode.
- **A local repo map / embedding-based retrieval.** Same reason
  v2-plan defers these — contracts are scope-narrow, this is
  solving a problem we don't have.
- **Hybrid routing (local for cheap turns, cloud for hard
  turns).** Clever, complicates observability, hard to gate
  cleanly on turn difficulty. Phase 4's mixed-mode closeout is
  the only cross-provider split worth its weight.

## See also

- `Providers.cs` — the switch this plan extends
- `v2-plan.md` phase 5 — the closeout pattern phase 4 here
  optionally composes with
- `v3-plan.md` — LSP tools are orthogonal; local + LSP is fine,
  no sequencing dependency
- `BRIEF.md` — "Non-Anthropic, non-Azure model backends" was a
  v1 non-goal; this plan is the conscious reversal once v2
  observability makes the experiment worth running
