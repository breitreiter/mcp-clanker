---
captured: 2026-05-10T21:01:26Z
repo: imp
source: cli
git-head: 8a742dfc8d03
---

Refinement to the static-vs-synthesis boundary (imp/learnings/static-vs-synthesis-boundary.md): cardinality is a second axis. The synthesis-vs-static call decides what can live in imp at all; the cardinality call decides whether it should. A synthesis task that runs 3 times per project ever doesn't earn the engineering payback of C# infrastructure (tools, prompts loader, state schema, retry, cost estimator), even if research-mode infrastructure exists to reuse. Concrete decision matrix: imp tidy runs frequently on every note batch — earns C#. /project-migrate runs ~3 times per project, shallow adoption tail — earns a skill, even though it's synthesis-bounded-by-structured-output like research mode is. Triggering example: 2026-05-10, Phase 2 of project-migrate was initially planned as imp migrate classify (C# command mirroring ResearchExecutor); user flagged the cardinality argument and we reverted to pure-skill Phase 2. The full rule: static + low-cardinality = skill, static + high-cardinality = imp, synthesis + low-cardinality = skill, synthesis + high-cardinality = imp.
