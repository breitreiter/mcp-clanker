using System.Text.RegularExpressions;

namespace McpClanker;

// Pre-flight danger check for bash commands. Returns a Classification with
// IsDangerous=true when the command matches a known danger pattern, or when
// it's a multi-line script containing a write-redirect or delete operation.
//
// Ported from nb/Shell/CommandClassifier.cs, stripped down. nb classifies
// for display (Read / Write / Append / Delete / Move / Copy / Run) because
// it shows the user a risk line and asks for approval. clanker is
// autonomous — it just needs a yes/no gate to decide whether to block the
// run. Everything else in nb's classifier was display-layer and is omitted.

public static class CommandClassifier
{
    // Patterns mirror nb's DefaultDangerPatterns exactly. Case-insensitive.
    // Order does not affect correctness; first match wins.
    static readonly string[] DefaultDangerPatterns =
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

    static readonly Regex[] DangerRegexes = DefaultDangerPatterns
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .ToArray();

    // For multi-line scripts: flag any line containing a write redirect (not
    // append `>>`) or a bare `rm`. `rm` alone isn't in the danger list above
    // (a one-liner `rm foo.txt` is usually fine), but inside a multi-line
    // script it's a smell worth blocking on.
    static readonly Regex WriteRedirectRx = new(@"(?<![>|])>\s*[^\s&|>]+", RegexOptions.Compiled);
    static readonly Regex DeleteRx = new(@"\brm\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public record Classification(bool IsDangerous, string? Reason);

    public static Classification Classify(string command)
    {
        var trimmed = command?.Trim() ?? "";
        if (trimmed.Length == 0)
            return new Classification(false, null);

        foreach (var rx in DangerRegexes)
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

        return new Classification(false, null);
    }
}
