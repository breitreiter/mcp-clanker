using Imp.Tools;

namespace Imp.Safety;

// Third safety gate: watch for the model spinning — repeating the same call
// or stacking failures — and terminate before it burns the full tool-call
// budget on nothing. Stateful (unlike CommandClassifier / NetworkEgressChecker
// which are pure pre-flight checks), but still a pure function over the
// tail of ExecutorState.RecentCalls; no side effects of its own.
//
// Thresholds come from TODO.md #2:
//   - N=3 same-tool-args in a row   → "stuck repeating"
//   - M=5 consecutive failures      → "banging its head"
//   - K=2 denied-network attempts   → deferred. In v1 the network-egress
//     gate is a hard block, so a single denied attempt already terminates
//     the run; K=2 only becomes reachable if that gate softens later.
//
// Category on trip: abandon. Reason: the model is stuck. Clarifying or
// revising the contract might help a rerun, but inside this run there's
// nothing more to do — the operator decides the remediation.

public static class DoomLoopDetector
{
    const int SameArgsThreshold = 3;
    const int ConsecutiveFailureThreshold = 5;

    public record Detection(bool Tripped, string? Reason, string? OffendingInput);

    public static Detection Check(IReadOnlyList<ToolCallRecord> recent)
    {
        if (recent.Count >= SameArgsThreshold)
        {
            var start = recent.Count - SameArgsThreshold;
            var first = recent[start];
            bool allSame = true;
            for (int i = start + 1; i < recent.Count; i++)
            {
                if (recent[i].Name != first.Name || recent[i].ArgsSignature != first.ArgsSignature)
                {
                    allSame = false;
                    break;
                }
            }
            if (allSame)
                return new Detection(
                    Tripped: true,
                    Reason: $"{SameArgsThreshold} consecutive calls to `{first.Name}` with identical arguments",
                    OffendingInput: $"{first.Name}({first.ArgsSignature})");
        }

        if (recent.Count >= ConsecutiveFailureThreshold)
        {
            var start = recent.Count - ConsecutiveFailureThreshold;
            bool allFailed = true;
            for (int i = start; i < recent.Count; i++)
            {
                if (recent[i].Success)
                {
                    allFailed = false;
                    break;
                }
            }
            if (allFailed)
                return new Detection(
                    Tripped: true,
                    Reason: $"{ConsecutiveFailureThreshold} consecutive tool-call failures",
                    OffendingInput: $"{recent[^1].Name}({recent[^1].ArgsSignature})");
        }

        return new Detection(false, null, null);
    }
}
