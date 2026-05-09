# Reference

Pointers to external systems — build instructions, tool READMEs,
third-party docs, configuration of services we depend on.

Truth lives in the doc; the substrate lints for drift between the
doc's claims and the actual scripts / configs.

## Example shape

```yaml
---
kind: reference
title: <short title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human
subject: <what external thing this references — e.g. "Azure deploy pipeline">
---

# <Reference title>

<Plain description of the external thing, what we use it for, and
where to find it.>

## Where to find it
- <link or path>

## Local interaction
- <commands, configs that touch this>
```

See `../_meta/conventions.md` for full conventions.
