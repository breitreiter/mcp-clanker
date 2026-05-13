---
captured: 2026-05-12T22:37:52Z
repo: imp
source: cli
git-head: fff33779a523
---

Tested qwen-coder (local qwen3-coder via Ollama) as the build executor. Failed under adversarial testing: given an intentionally vague contract and no nuget access, it hallucinated libraries and API shapes, then self-validated the result as correct. The self-validation step is the dangerous part — without external nuget resolution to ground-truth-check imports, the model can't distinguish 'I know this library exists' from 'I confidently assert this library exists.' Pairs with the existing learning about qwen-coder not being viable as a build executor; this is the specific adversarial-testing evidence behind that call.
