using System.Text.RegularExpressions;

namespace Imp;

// Task descriptor — what the executor sees. Lifted from the lead-agent prompt
// pattern in Anthropic's multi-agent research system. v1's research loop is
// single-executor, but the descriptor shape gives a clean upgrade path to
// multi-executor without redesigning the contract.
//
// The bare-minimum field is `Question`. Everything else is optional and
// degrades to empty when the caller used free-text instead of --brief.

public record TaskDescriptor(
    string ResearchId,        // e.g. "R-007"
    string Slug,              // e.g. "scope-adherence-enforcement"
    string Question,
    IReadOnlyList<string> SubQuestions,
    IReadOnlyList<string> SuggestedSources,
    IReadOnlyList<string> Forbidden,
    string Background,
    string ExpectedOutput,
    string SourceMarkdown);

public static class BriefParser
{
    // "## R-NNN: title"
    static readonly Regex TitleRx = new(@"^##\s*(R-\d+)\s*:\s*(.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // "**Question:** text" — single-line or up to a blank line.
    static readonly Regex QuestionRx = new(@"\*\*Question:\*\*\s*(.+?)(?=\r?\n\r?\n|\r?\n\*\*|\z)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    static readonly Regex BulletRx = new(@"^\s*-\s*(.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static TaskDescriptor ParseFile(string path, string repoRoot)
    {
        var markdown = File.ReadAllText(path);
        var titleMatch = TitleRx.Match(markdown);
        var researchId = titleMatch.Success ? titleMatch.Groups[1].Value : AllocateNextId(repoRoot);
        var titleText = titleMatch.Success ? titleMatch.Groups[2].Value.Trim() : "";

        var question = QuestionRx.Match(markdown) is { Success: true } qm
            ? CollapseWhitespace(qm.Groups[1].Value)
            : titleText;

        if (string.IsNullOrWhiteSpace(question))
            throw new InvalidOperationException(
                $"Brief at {path} has no **Question:** section and no parseable title — at least one is required.");

        var subQuestions = BulletsIn(Section(markdown, "Sub-questions"));
        var sources = BulletsIn(Section(markdown, "Sources"));
        var forbidden = BulletsIn(Section(markdown, "Forbidden"));
        var background = Section(markdown, "Background")?.Trim() ?? "";
        var expectedOutput = Section(markdown, "Output")?.Trim() ?? "";

        return new TaskDescriptor(
            ResearchId: researchId,
            Slug: SlugFrom(titleText.Length > 0 ? titleText : question),
            Question: question,
            SubQuestions: subQuestions,
            SuggestedSources: sources,
            Forbidden: forbidden,
            Background: background,
            ExpectedOutput: expectedOutput,
            SourceMarkdown: markdown);
    }

    public static TaskDescriptor FromFreeText(string question, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new InvalidOperationException("Research question is empty.");

        var researchId = AllocateNextId(repoRoot);
        var slug = SlugFrom(question);
        // Synthesize a brief that round-trips through the archive, so the
        // sidecar brief.md is human-readable and not just a one-liner.
        var synthMarkdown = $"## {researchId}: {SlugTitle(question)}\n\n**Question:** {question}\n";
        return new TaskDescriptor(
            ResearchId: researchId,
            Slug: slug,
            Question: question.Trim(),
            SubQuestions: Array.Empty<string>(),
            SuggestedSources: Array.Empty<string>(),
            Forbidden: Array.Empty<string>(),
            Background: "",
            ExpectedOutput: "",
            SourceMarkdown: synthMarkdown);
    }

    // Walk <repo-parent>/<repo>.researches/ and pick the next R-NNN. Falls
    // back to R-001 if the directory is empty or missing. Three-digit zero
    // padding keeps directory listings sorted lexicographically.
    public static string AllocateNextId(string repoRoot)
    {
        var researchesDir = ResearchArchive.RootFor(repoRoot);
        if (!Directory.Exists(researchesDir)) return "R-001";

        int max = 0;
        var rx = new Regex(@"^R-(\d+)(?:-|$)", RegexOptions.Compiled);
        foreach (var entry in Directory.EnumerateDirectories(researchesDir))
        {
            var name = Path.GetFileName(entry);
            var m = rx.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
                max = Math.Max(max, n);
        }
        return $"R-{(max + 1):D3}";
    }

    static string? Section(string markdown, string name)
    {
        var pattern = @$"\*\*{Regex.Escape(name)}:\*\*\s*\r?\n?(.+?)(?=\r?\n\*\*[^*]+:\*\*|\z)";
        var m = Regex.Match(markdown, pattern, RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : null;
    }

    static IReadOnlyList<string> BulletsIn(string? section) =>
        section is null
            ? Array.Empty<string>()
            : BulletRx.Matches(section).Select(m => m.Groups[1].Value.Trim()).ToList();

    static string CollapseWhitespace(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    // Slug: lowercase, alphanum + hyphen, max 40 chars. Used in the archive
    // directory name (R-NNN-<slug>/) so eyeballing a directory listing is
    // useful — "R-007-scope-adherence-enforcement" beats "R-007".
    public static string SlugFrom(string source)
    {
        var lowered = source.ToLowerInvariant();
        var cleaned = Regex.Replace(lowered, @"[^a-z0-9]+", "-").Trim('-');
        if (cleaned.Length > 40) cleaned = cleaned[..40].TrimEnd('-');
        return string.IsNullOrEmpty(cleaned) ? "research" : cleaned;
    }

    static string SlugTitle(string question)
    {
        var trimmed = question.Trim().TrimEnd('?', '.', '!');
        if (trimmed.Length > 60) trimmed = trimmed[..60].TrimEnd() + "…";
        return trimmed;
    }
}
