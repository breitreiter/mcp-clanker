---
kind: learning
title: embeddings as substrate glue beyond search
created: 2026-05-13
updated: 2026-05-13
status: current
touches:
  files: []
  symbols: []
  features: [embeddings, deduplication, concept-generation, cross-rule-linting, substrate-accrual]
provenance:
  author: imp-gnome
  origin: note:2026-05-10-190526-embeddings-phase-1-5-of-project-migrate
---

# embeddings as substrate glue beyond search

Embeddings serve as semantic glue within the substrate, enabling long-term maintainability by preserving relatedness across years of accumulated knowledge; without them, the system's accretive model fails to surface relevant connections, making retrieval reliant on fragile filename memory alone.  

**Why:** The substrate's ability to evolve meaningfully over time depends on semantic relatedness, which embeddings provide beyond simple keyword matching.  

**How to apply:** Implement embeddings when multiple advanced use cases—such as note-time deduplication, concept-page generation, or cross-rule linting—become part of the design scope, rather than building them for migration alone.
