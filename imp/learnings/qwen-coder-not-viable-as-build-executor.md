---
kind: learning
title: qwen3-coder hallucinates API shape on imp's codebase; route to research-only
created: 2026-05-10
updated: 2026-05-13
status: current
touches:
  files: []
  features: [executor-selection, qwen, mode-config]
provenance:
  author: imp-gnome
topics: [executor-selection, qwen]
---

# qwen3-coder is research-only on this codebase

qwen3-coder running locally hallucinates API shape when used as a
build executor against imp itself — it produces code that calls
methods that don't exist on imp's types. It's still useful for
research mode (which is read-only and citation-grounded), but not
for build.

**Why:** the codebase is small enough that a coder model should
handle it, but qwen3-coder doesn't reliably ground its output in
the actual symbols present. The citation contract in research mode
acts as a forcing function the build mode lacks. Adversarial testing
revealed that without external nuget resolution to ground-truth-check imports, the model self-validates hallucinated libraries and API shapes — confidently asserting correctness without external verification. This self-validation step is the dangerous part: the model cannot distinguish "I know this library exists" from "I confidently assert this library exists." (updated: earlier framing said the model lacks grounding; current finding is that it actively self-validates hallucinations under no external grounding.)

**How to apply:** route qwen3-coder to research mode only. This
implies imp needs per-mode model configuration — research can use
the local qwen, build keeps using Azure GPT-5.1-codex-mini.
Switching to qwen3-32B as the local research executor frees the GPU
to run qwen3-embedding concurrently on Strix Halo, which is a cheap
side-benefit if the substrate later wants embeddings.
