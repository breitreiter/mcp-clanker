---
captured: 2026-05-10T19:05:26Z
repo: imp
source: cli
git-head: 8a742dfc8d03
---

Embeddings (Phase 1.5 of project-migrate, eventually a shared 'imp embed' primitive) are substrate GLUE, not just search. The substrate's accretive-over-years model breaks without semantic relatedness: by year 2 you have 200 learnings and only filename memory to find 'the one about X.' Glue use cases that justify the build: (1) note-time dedup — embed each capture, suggest 'this updates learnings/foo.md' instead of creating a parallel entry, otherwise the substrate accretes duplicates; (2) migration polish-trap — single-doc Phase 2 classification literally cannot see 'X is historical relative to Y' without clustering; (3) concept-page generation — imp/concepts/<topic>.md needs auto-clustering, hand-tagging is brittle and under-tags; (4) substrate-aware imp research — embed the question, prime the prompt with related entries so the model doesn't re-derive things; (5) cross-rule lint — flag rules that are about the same thing without sharing keywords. Search alone (qwen can grep) is the weakest of these. Timing: build when a SECOND consumer materializes (spec says this; instinct is right). Migration alone doesn't justify the design moment of choosing cloud vs local, cache shape, 'imp embed' API. Once note-dedup or concept-synthesis is also being designed, that's the trigger.
