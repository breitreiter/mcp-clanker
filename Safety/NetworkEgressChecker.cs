using System.Text.RegularExpressions;

namespace Imp.Safety;

// Second safety gate: intercept bash commands that would make network calls
// off the host. Pre-flight check mirrors CommandClassifier's shape — a pure
// static Check returning (IsBlocked, Reason). RunBash flags state.SafetyBreach
// with blocked_question.category=rescope_or_capability on hit. A contract
// that genuinely needs network must declare it via `**Allowed network:**`
// in the contract markdown — when present, RunBash bypasses this gate
// entirely (see Tools.RunBash). The bullets in that section are documented
// intent for human readers, not validated against specific commands.
//
// Exemption: any command mentioning a localhost marker (localhost,
// 127.0.0.1, ::1, 0.0.0.0) passes through so local dev-server testing works
// without ceremony. The exemption is coarse — a URL query string that
// happens to contain "localhost" would sneak through — but the threat
// model here is "confused/misaligned model," not "user bypassing the check
// with a crafted command," so the coarse check is fine for v1.

public static class NetworkEgressChecker
{
    public record Result(bool IsBlocked, string? Reason);

    // Raw network tools — exfil or remote write capable. Always suspect
    // unless a localhost marker is present.
    static readonly Regex[] NetworkTools =
    [
        new(@"\bcurl\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bwget\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bnc\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bncat\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bssh\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bscp\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bsftp\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\brsync\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bftp\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\btelnet\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    // GitHub CLI mutations. `gh api` can do anything (read or write) so it's
    // blocked regardless; other subcommands are blocked only on mutation verbs.
    // Read-only `gh` (e.g. `gh pr view`, `gh issue list`) passes through.
    static readonly Regex[] GhMutations =
    [
        new(@"\bgh\s+api\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bgh\s+\w+\s+(create|edit|delete|close|merge|reopen|archive|unarchive|upload|comment|review|ready|lock|unlock|pin|unpin|fork|rename|sync)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    static readonly string[] LocalhostMarkers =
    [
        "localhost",
        "127.0.0.1",
        "::1",
        "0.0.0.0",
    ];

    public static Result Check(string command)
    {
        var trimmed = command?.Trim() ?? "";
        if (trimmed.Length == 0)
            return new Result(false, null);

        // gh mutations first — more specific than the generic tool patterns.
        foreach (var rx in GhMutations)
        {
            var m = rx.Match(trimmed);
            if (m.Success)
                return new Result(true, $"gh mutation command `{m.Value}` is blocked; contract must declare network access");
        }

        foreach (var rx in NetworkTools)
        {
            var m = rx.Match(trimmed);
            if (m.Success)
            {
                if (IsLocalhostOnly(trimmed))
                    return new Result(false, null);
                return new Result(true, $"network tool `{m.Value}` detected; contract does not declare network access");
            }
        }

        return new Result(false, null);
    }

    static bool IsLocalhostOnly(string command)
    {
        foreach (var marker in LocalhostMarkers)
            if (command.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
