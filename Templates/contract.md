## T-NNN: short descriptive title

**Goal:** One sentence. What changes in the world when this is done.

**Scope:**
- create: path/to/new/file.ts
- edit: path/to/existing/file.ts
- edit: path/to/tests/file.test.ts

**Contract:**
- Exported function signatures, types, or APIs that will exist.
- Key behaviors for important inputs.
- Behavior for edge cases.
- Purity / side-effect constraints.

**Context:**
- path/to/related.ts — why it matters (one-liner).
- path/to/another.ts — why it matters (one-liner).

**Acceptance:**
- All existing tests pass.
- New tests cover: case A, case B, case C.
- Docs updated at path/to/doc.md if public API changed.
- No changes to files outside Scope.

**Non-goals:**
- This task does NOT do X (that's T-NNN).
- This task does NOT do Y.

**Allowed network:** *(optional — omit unless the task genuinely needs network)*
- example.com — fetching the spec used to drive code generation
- pkg.example.org — package-manager refresh

**Depends on:** T-NNN, T-NNN  (or "none")
