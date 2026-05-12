---
kind: rule
title: Embeddings come from local Qwen3 Embedding 8B at imp:8081
created: 2026-05-11
updated: 2026-05-11
status: current
touches:
  files: [Infrastructure/Providers.cs, Substrate/Tidy.cs, imp/_meta/embeddings.jsonl]
  features: [embeddings, substrate]
provenance:
  author: human
enforces:
  - "Infrastructure/**/*.cs"
---

# Embedding provider is locked

All substrate embeddings come from **Qwen3 Embedding 8B** served
OpenAI-compatibly at **`imp:8081`** (local box, on the home LAN).
Vector dimension: **4096**.

Do not add a Cohere, OpenAI, Voyage, Anthropic, or any other
embedding client. Do not add a hosted-provider fallback when
`imp:8081` is unreachable — fail closed.

**Why:** The embeddings cache at `imp/_meta/embeddings.jsonl` is
dimension- and provider-locked. Mixing vector spaces silently
corrupts nearest-neighbor results — there's no runtime error,
just wrong matches. A "fallback to OpenAI" path is therefore
worse than a hard failure: degraded correctness with no signal.
Local + free + no-exfiltration is gravy on top, but the real
constraint is cache integrity.

**How to apply:**
- New embedding code reads provider config from `appsettings.json`
  pointing at `imp:8081`. There is no provider-selection switch.
- If `imp:8081` is unreachable, tidy (and any other consumer)
  exits non-zero with a clear message. No silent fallback.
- If the model ever changes (different Qwen3 variant, different
  family entirely), regenerate the entire cache atomically;
  partial-mixed caches are forbidden. The dimension change alone
  would corrupt the jsonl format, but a same-dim swap (e.g.
  Qwen3 4B → 8B if both happen to match) wouldn't — hence this rule.
