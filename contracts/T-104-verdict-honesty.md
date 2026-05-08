## T-104: Don't return Success when acceptance is unverified or scope drifted

**Goal:** `BuildResult.TerminalState=Success` must mean "all reported acceptance bullets passed AND no out-of-scope file was touched." Today it can be Success with `Unknown` acceptance bullets or `in_scope: false` — the parent's auto-commit then ships partial work as if it were verified.

**Scope:**
- edit: Executor.cs

**Contract:**

Two new demotion triggers, applied alongside the existing closeout-fail demotion at `Executor.cs:269-277`. The order doesn't matter; what matters is that all three checks fire before `trace.WriteEnd` in the finally block (so trace, transcript, and POW agree on the final state — same constraint as the comment at `Executor.cs:264-268`).

The triggers:

1. **Unknown acceptance.** If the authoritative acceptance source (`state.CloseoutReports ?? state.AcceptanceReports`) contains any `Unknown` verdict, demote `Success`. The model said "I can't tell" — that's not Success.

2. **Out-of-scope file touched.** Compute scope adherence (the existing `CheckScopeAdherence` helper does this — feel free to lift the call earlier, or inline the check, or use `state.FilesTouched` directly). If any file outside the contract's declared Scope was touched, demote `Success`.

What to demote *to* is your call. Two reasonable shapes:
- Reuse `Failure` for both (simpler, consistent with the closeout-fail path).
- Use `Blocked` (signals "needs a parent look" more accurately than "the work failed") and populate `BlockedQuestion` with a synthetic summary naming the cause.

Whichever you pick, populate `notes` (or `BlockedQuestion.Summary`) with a one-line cause string the parent can read without diving into the diff — e.g. `"Verdict downgrade: 1 of 3 acceptance items unverified (Unknown)"` or `"Verdict downgrade: scope drift — touched [Imp.csproj] outside declared scope"`. Be explicit about which trigger fired.

The existing closeout-fail demotion stays as-is.

**Context:**
- `Executor.cs:227-241` — self-check phase populates `state.AcceptanceReports` (no demotion today).
- `Executor.cs:243-278` — closeout phase, with the existing Fail-only demotion at 269-277. New triggers go alongside.
- `Executor.cs:295-348` — scope adherence is computed *after* the finally block, on the way into the BuildResult. The trigger needs to consult it (or its inputs) before then.
- `BuildResult.cs` — `TerminalState`, `AcceptanceStatus.Unknown`, `BlockedQuestion`, `BlockedCategory` already exist. No schema changes needed.
- T-101's proof-of-work at `imp.worktrees/T-101.trace/proof-of-work.json` is the live example: terminal=Success, in_scope=false, one Unknown acceptance. After this contract, that exact run shape produces a non-Success terminal.

**Acceptance:**
- A run with all-Pass acceptance and zero out-of-scope files still produces `terminal_state: Success` (regression bar).
- A run with any Unknown acceptance produces a non-Success terminal (Failure or Blocked, contract author's choice).
- A run with out-of-scope file touches produces a non-Success terminal.
- The downgrade fires before `trace.WriteEnd`, so trace / transcript / proof-of-work all agree.
- `notes` (or `BlockedQuestion.Summary`) names which trigger fired and what the offending input was (the Unknown bullets, or the out-of-scope paths).
- `dotnet build` succeeds.
- No changes to files outside Scope.

**Non-goals:**
- Does NOT change the `BuildResult` schema (no new enum values, no new fields).
- Does NOT change the closeout reviewer's logic — closeout still overrides self-report acceptance, still demotes on Fail.
- Does NOT change `McpTools.Build`'s auto-commit gate — it already gates on `terminal == Success`, so this contract just causes auto-commit to skip in the demoted cases (desired behavior).
- Does NOT add a test project. imp has no test infra; verify by code review and by running an existing contract end-to-end.
