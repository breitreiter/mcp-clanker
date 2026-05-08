using System.Text.RegularExpressions;

namespace Imp;

// Pre-flight gate for bash commands. Two tiers:
//   - **Imp invariants** (always-on regardless of sandbox mode): patterns
//     the executor must never run because they break imp's own contracts,
//     not because they're unsafe. Today: `git add` / `git commit`, because
//     the auto-commit on evaluator sign-off (McpTools.Build) is the
//     authoritative committer. The model committing would create
//     half-staged trees and double-commits.
//   - **Host-only safety** (Host mode only): filesystem / privilege /
//     exfil heuristics. Under Docker mode these become redundant — the
//     bind-mounted `/work`, `--network=none`, `--rm`, and resource limits
//     are the structural barrier — and prone to false positives (e.g. a
//     benign multi-line script that cleans up its own `$(mktemp)`).
//
// Ported from nb/Shell/CommandClassifier.cs, stripped down. nb classifies
// for display (Read / Write / Append / Delete / Move / Copy / Run) because
// it shows the user a risk line and asks for approval. imp is
// autonomous — it just needs a yes/no gate. Everything else in nb's
// classifier was display-layer and is omitted.

public static class CommandClassifier
{
    // Always-on. Imp behavioral invariants — model must not commit.
    static readonly string[] ImpInvariantPatterns =
    {
        @"\bgit\s+add\b",
        @"\bgit\s+commit\b",
    };

    // Host-mode only. Safety heuristics that the docker layer makes
    // redundant: privilege escalation, raw block-device ops, root-fs
    // writes, network-piped install patterns, and recursive deletes.
    static readonly string[] HostDangerPatterns =
    {
        @"\brm\s+-r",
        @"\brm\s+-rf",
        @"\brm\s+-fr",
        @"\bsudo\b",
        @"\bdd\b",
        @"\bchmod\s+777",
        @"\bchmod\s+-R",
        @"\bcurl\b.*\|\s*sh",
        @"\bcurl\b.*\|\s*bash",
        @"\bwget\b.*\|\s*sh",
        @"\bwget\b.*\|\s*bash",
        @"\bmkfs\b",
        @"\bfdisk\b",
        @">\s*/dev/(?!null)",
        @">\s*/etc/",
        @">\s*/usr/",
        @">\s*/bin/",
        @">\s*/sbin/",
    };

    static readonly Regex[] ImpInvariantRegexes = ImpInvariantPatterns
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .ToArray();

    static readonly Regex[] HostDangerRegexes = HostDangerPatterns
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .ToArray();

    // Multi-line script smells, host-only. A bare `rm` or a write redirect
    // hidden inside a script wall is a smell when it could touch real host
    // state. Inside a Docker container (--rm, /work the only persistent
    // mount), `rm` is bounded to the worktree and ephemeral container fs —
    // exactly the scope the contract is allowed to mutate.
    static readonly Regex WriteRedirectRx = new(@"(?<![>|])>\s*[^\s&|>]+", RegexOptions.Compiled);
    static readonly Regex DeleteRx = new(@"\brm\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public record Classification(bool IsDangerous, string? Reason);

    public static Classification Classify(string command, SandboxMode mode)
    {
        var trimmed = command?.Trim() ?? "";
        if (trimmed.Length == 0)
            return new Classification(false, null);

        foreach (var rx in ImpInvariantRegexes)
        {
            var m = rx.Match(trimmed);
            if (m.Success)
                return new Classification(true, $"matches imp invariant `{rx}` (matched: `{m.Value}`)");
        }

        if (mode == SandboxMode.Host)
        {
            foreach (var rx in HostDangerRegexes)
            {
                var m = rx.Match(trimmed);
                if (m.Success)
                    return new Classification(true, $"matches danger pattern `{rx}` (matched: `{m.Value}`)");
            }

            var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > 1)
            {
                if (lines.Any(l => DeleteRx.IsMatch(l)))
                    return new Classification(true, "multi-line script contains a delete operation (rm)");
                if (lines.Any(l => WriteRedirectRx.IsMatch(l)))
                    return new Classification(true, "multi-line script contains a write redirect (>)");
            }
        }

        return new Classification(false, null);
    }
}
