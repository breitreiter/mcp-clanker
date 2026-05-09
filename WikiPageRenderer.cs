using System.Globalization;
using System.Text;

namespace Imp;

// Step 5 of project/wiki-plan.md. Pure function from
// (WikiPageContext, WikiManifestEntry, ResearchReport?) to a markdown string.
// No filesystem I/O, no model calls. Three rendering branches:
//
//   - Generated: a successful research run; page body is built from the
//     report's synthesis, findings, coverage, conflicts.
//   - Oversized: the planner flagged the dir over Wiki:MaxDirBytes; no
//     report was generated. Stub explains the gap and points the user at
//     subtree invocation.
//   - Failed: the dispatch returned a terminal-error envelope. Stub
//     records the error and points at the research archive for triage.
//
// Findings whose primary citation falls inside the target dir are rendered
// under "Contents"; findings citing outside the target go under
// "Cross-references". v0 always links citations to source files (relative
// paths from the page); cross-link enrichment to other wiki pages is
// deferred to the orchestrator wiring (post-step-7).

public sealed record WikiPageContext(
    string PagePath,
    string SourcePath,
    string SourceTreeSha,
    long SourceBytes,
    int FileCount,
    long MaxDirBytes,
    string Mode,
    string? ModelName,
    string? ProviderName,
    string? ResearchId,
    int GeneratorVersion,
    DateTimeOffset GeneratedAt,
    bool? WorktreeDirty);

public static class WikiPageRenderer
{
    public const int CurrentGeneratorVersion = 0;

    public static string Render(WikiPageContext ctx, WikiManifestEntry entry, ResearchReport? report)
    {
        if (entry.Decision == WikiDecision.Stub)
            return RenderOversized(ctx);
        if (entry.Status == WikiEntryStatus.Failed)
            return RenderFailed(ctx, entry.Error ?? "unknown");
        if (report is null)
            throw new InvalidOperationException(
                $"Cannot render page for {entry.SourcePath}: status={entry.Status} but no report supplied.");
        return RenderGenerated(ctx, report);
    }

    public static string RenderGenerated(WikiPageContext ctx, ResearchReport report)
    {
        var sb = new StringBuilder();
        var extras = new List<(string, string)>
        {
            ("synthesis_summary", QuoteYaml(SummariseSynthesis(report.Synthesis))),
        };
        WriteFrontmatter(sb, ctx, status: "generated", extras: extras);
        WriteHeading(sb, ctx);

        if (!string.IsNullOrWhiteSpace(report.Synthesis))
        {
            sb.Append("> ").Append(report.Synthesis.Trim().Replace("\n", "\n> ")).Append("\n\n");
        }

        var (inTarget, outOfTarget) = SplitFindingsByLocation(report.Findings, ctx.SourcePath);
        var pageDepth = PageDepth(ctx.PagePath);

        if (inTarget.Count > 0)
        {
            sb.Append("## Contents\n\n");
            foreach (var grouping in GroupByPrimaryCitationFile(inTarget))
            {
                foreach (var f in grouping)
                    AppendFindingBullet(sb, f, ctx.SourcePath, pageDepth, isCrossRef: false);
            }
            sb.Append('\n');
        }

        if (outOfTarget.Count > 0)
        {
            sb.Append("## Cross-references\n\n");
            foreach (var f in outOfTarget)
                AppendFindingBullet(sb, f, ctx.SourcePath, pageDepth, isCrossRef: true);
            sb.Append('\n');
        }

        var openItems = new List<string>();
        if (report.Coverage?.Gaps is { Count: > 0 } gaps) openItems.AddRange(gaps);
        if (report.FollowUps is { Count: > 0 } follows) openItems.AddRange(follows);
        if (openItems.Count > 0)
        {
            sb.Append("## Open questions\n\n");
            foreach (var item in openItems) sb.Append("- ").Append(item).Append('\n');
            sb.Append('\n');
        }

        if (report.Conflicts is { Count: > 0 } conflicts)
        {
            sb.Append("## Conflicts\n\n");
            foreach (var c in conflicts)
            {
                sb.Append("- **").Append(c.Claim).Append("**\n");
                if (!string.IsNullOrWhiteSpace(c.Resolution))
                    sb.Append("  - Resolution: ").Append(c.Resolution).Append('\n');
                if (!string.IsNullOrWhiteSpace(c.Reasoning))
                    sb.Append("  - Reasoning: ").Append(c.Reasoning).Append('\n');
            }
            sb.Append('\n');
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    public static string RenderOversized(WikiPageContext ctx)
    {
        var sb = new StringBuilder();
        var extras = new List<(string, string)> { ("threshold", ctx.MaxDirBytes.ToString(CultureInfo.InvariantCulture)) };
        WriteFrontmatter(sb, ctx, status: "oversized", extras: extras);
        WriteHeading(sb, ctx);

        sb.Append("No wiki page was generated for this directory: total source bytes (")
          .Append(ctx.SourceBytes.ToString(CultureInfo.InvariantCulture))
          .Append(") exceed the v0 threshold (")
          .Append(ctx.MaxDirBytes.ToString(CultureInfo.InvariantCulture))
          .Append("). Adaptive splitting will land in a future version. For now, run `imp wiki ");
        sb.Append(ctx.SourcePath.Length == 0 ? "<subdir>" : $"{ctx.SourcePath}/<subdir>");
        sb.Append("` against subdirectories individually if you need coverage here.\n");

        return sb.ToString();
    }

    public static string RenderFailed(WikiPageContext ctx, string error)
    {
        var sb = new StringBuilder();
        var extras = new List<(string, string)> { ("error", QuoteYaml(error)) };
        WriteFrontmatter(sb, ctx, status: "failed", extras: extras);
        WriteHeading(sb, ctx);

        sb.Append("The research run for this directory did not complete successfully.\n\n");
        sb.Append("- Error: ").Append(error).Append('\n');
        if (!string.IsNullOrEmpty(ctx.ResearchId))
            sb.Append("- Research archive: `").Append(ctx.ResearchId).Append("` (see `<repo>.researches/")
              .Append(ctx.ResearchId).Append("-*/transcript.md` for the executor trace).\n");
        sb.Append("\nRe-run `imp wiki ");
        sb.Append(ctx.SourcePath.Length == 0 ? "" : ctx.SourcePath);
        sb.Append("` to retry. If the failure repeats, tighten the wiki system prompt or raise `Wiki:ToolBudget`.\n");
        return sb.ToString();
    }

    static void WriteFrontmatter(StringBuilder sb, WikiPageContext ctx, string status, IReadOnlyList<(string Key, string Value)>? extras)
    {
        sb.Append("---\n");
        sb.Append("source_path: ").Append(ctx.SourcePath.Length == 0 ? "\"\"" : ctx.SourcePath).Append('\n');
        sb.Append("source_tree_sha: ").Append(ctx.SourceTreeSha).Append('\n');
        sb.Append("source_files_count: ").Append(ctx.FileCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("source_bytes: ").Append(ctx.SourceBytes.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("generated_at: ").Append(ctx.GeneratedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("generator_version: ").Append(ctx.GeneratorVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("mode: ").Append(ctx.Mode).Append('\n');
        if (!string.IsNullOrEmpty(ctx.ModelName))
            sb.Append("model: ").Append(ctx.ModelName).Append('\n');
        if (!string.IsNullOrEmpty(ctx.ResearchId))
            sb.Append("research_id: ").Append(ctx.ResearchId).Append('\n');
        sb.Append("status: ").Append(status).Append('\n');
        if (ctx.WorktreeDirty == true)
            sb.Append("worktree_dirty: true\n");
        if (extras is not null)
            foreach (var (k, v) in extras) sb.Append(k).Append(": ").Append(v).Append('\n');
        sb.Append("---\n\n");
    }

    static void WriteHeading(StringBuilder sb, WikiPageContext ctx)
    {
        var title = ctx.SourcePath.Length == 0 ? "(repo root)" : ctx.SourcePath;
        sb.Append("# ").Append(title).Append("\n\n");
    }

    static (List<Finding> InTarget, List<Finding> OutOfTarget) SplitFindingsByLocation(
        IReadOnlyList<Finding> findings, string sourcePath)
    {
        var inTarget = new List<Finding>();
        var outOfTarget = new List<Finding>();
        foreach (var f in findings)
        {
            var primary = f.Citations?.FirstOrDefault(c => c.Kind == CitationKind.File);
            if (primary is null || string.IsNullOrEmpty(primary.Path))
            {
                inTarget.Add(f);
                continue;
            }
            (CitationIsInTarget(primary, sourcePath) ? inTarget : outOfTarget).Add(f);
        }
        return (inTarget, outOfTarget);
    }

    static IEnumerable<IGrouping<string, Finding>> GroupByPrimaryCitationFile(IReadOnlyList<Finding> findings)
    {
        return findings.GroupBy(f =>
            f.Citations?.FirstOrDefault(c => c.Kind == CitationKind.File)?.Path ?? "");
    }

    static bool CitationIsInTarget(Citation c, string sourcePath)
    {
        if (c.Path is null) return false;
        var path = c.Path.Replace('\\', '/').TrimStart('/');
        if (sourcePath.Length == 0)
            return !path.Contains('/');
        return path == sourcePath
            || path.StartsWith(sourcePath + "/", StringComparison.Ordinal);
    }

    static void AppendFindingBullet(StringBuilder sb, Finding f, string sourcePath, int pageDepth, bool isCrossRef)
    {
        var primary = f.Citations?.FirstOrDefault(c => c.Kind == CitationKind.File);
        var displayName = primary?.Path is null
            ? "(no citation)"
            : isCrossRef
                ? primary.Path
                : RelativeToTarget(primary.Path, sourcePath);

        sb.Append("- `").Append(displayName).Append("` — ").Append(f.Claim.Trim());

        var fileCitations = f.Citations?.Where(c => c.Kind == CitationKind.File).ToList() ?? new List<Citation>();
        if (fileCitations.Count > 0)
        {
            sb.Append(' ');
            for (int i = 0; i < fileCitations.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(FormatCitationLink(fileCitations[i], pageDepth));
            }
        }
        sb.Append('\n');

        if (!string.IsNullOrWhiteSpace(f.Reasoning))
            sb.Append("  *").Append(f.Reasoning.Trim()).Append("*\n");
    }

    static string FormatCitationLink(Citation c, int pageDepth)
    {
        var path = (c.Path ?? "").Replace('\\', '/').TrimStart('/');
        var anchor = "";
        if (c.LineStart.HasValue)
        {
            anchor = $"#L{c.LineStart.Value}";
            if (c.LineEnd.HasValue && c.LineEnd.Value != c.LineStart.Value)
                anchor += $"-L{c.LineEnd.Value}";
        }
        var prefix = pageDepth == 0 ? "./" : string.Concat(Enumerable.Repeat("../", pageDepth));
        var label = c.LineStart.HasValue
            ? (c.LineEnd.HasValue && c.LineEnd.Value != c.LineStart.Value
                ? $"L{c.LineStart}-L{c.LineEnd}"
                : $"L{c.LineStart}")
            : Path.GetFileName(path);
        return $"[{label}]({prefix}{path}{anchor})";
    }

    static string RelativeToTarget(string citationPath, string sourcePath)
    {
        var path = citationPath.Replace('\\', '/').TrimStart('/');
        if (sourcePath.Length == 0) return path;
        if (path.StartsWith(sourcePath + "/", StringComparison.Ordinal))
            return path.Substring(sourcePath.Length + 1);
        return path;
    }

    // Number of `..` segments needed from the page path back to the repo root.
    // pagePath = "wiki/Foo.md"        → 1
    // pagePath = "wiki/src/Foo.md"    → 2
    // pagePath = "wiki/README.md"     → 1
    static int PageDepth(string pagePath)
    {
        var slashes = 0;
        foreach (var c in pagePath.Replace('\\', '/'))
            if (c == '/') slashes++;
        return slashes;
    }

    // Defensive YAML quoting: double-quote and escape backslashes/quotes/newlines.
    // Used only for free-form strings (e.g. error messages); structured fields
    // emit unquoted so the planner's regex frontmatter reader keeps working.
    static string QuoteYaml(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        return $"\"{escaped}\"";
    }

    // Synthesis summary for the index: collapse whitespace, truncate to ~140
    // chars at a word boundary. The full synthesis stays in the page body —
    // this is purely so WikiIndexRenderer doesn't need to re-parse the body.
    const int SynthesisSummaryMaxChars = 140;
    static string SummariseSynthesis(string synthesis)
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(synthesis ?? "", @"\s+", " ").Trim();
        if (collapsed.Length <= SynthesisSummaryMaxChars) return collapsed;
        var slice = collapsed[..SynthesisSummaryMaxChars];
        var lastSpace = slice.LastIndexOf(' ');
        if (lastSpace > SynthesisSummaryMaxChars * 3 / 4) slice = slice[..lastSpace];
        return slice.TrimEnd(',', '.', ';', ':') + "…";
    }
}
