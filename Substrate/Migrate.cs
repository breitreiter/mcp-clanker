using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Imp.Substrate;

// `imp migrate [paths...]` — Phase 1 of project-migrate (the deterministic
// phase). Walks legacy markdown sources, gathers per-doc signals (via
// Substrate/Signals.cs), heuristic-classifies each doc's shape, and writes
// a migration plan that a future Phase 2 (cloud-subagent classification)
// will consume.
//
// No model calls. Pure git + filesystem + regex.
//
// Source spec: project/project-migrate-spec.md
// Build plan:  plans/project-migrate-phase1.md
//
// Not implemented in this phase (per build plan):
//   - Phase 1.5 embeddings / clustering (deferred until shared `imp embed`)
//   - Phase 2 classification, proposal drafting, cleanup proposal
//   - --resume, --one-at-a-time
//   - Cost estimator (no model calls yet → no cost)
public static class Migrate
{
    // Heuristic shape labels. Order in DocShape mirrors the order rules
    // are applied in Sniff() (first-match-wins).
    public enum DocShape
    {
        TaskList,    // checklist-shaped content
        Reference,   // external-system docs
        PlanShaped,  // single coherent plan / spec / design
        Mixed,       // multiple H2s of different kinds
        Flavor,      // narrative / lore / aspiration
        Unknown,     // graceful fallthrough
    }

    public sealed record DocRecord(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("shape")] DocShape Shape,
        [property: JsonPropertyName("sniff_reason")] string SniffReason,
        [property: JsonPropertyName("proposed_action")] string ProposedAction,
        [property: JsonPropertyName("signals")] SignalsReport Signals);

    public sealed record MigrationPlan(
        [property: JsonPropertyName("migration_id")] string MigrationId,
        [property: JsonPropertyName("started_at")] string StartedAt,
        [property: JsonPropertyName("repo_root")] string RepoRoot,
        [property: JsonPropertyName("sources")] IReadOnlyList<string> Sources,
        [property: JsonPropertyName("docs")] IReadOnlyList<DocRecord> Docs);

    public static int Run(string[] args)
    {
        // ── arg parsing ─────────────────────────────────────────
        var explicitPaths = new List<string>();
        var include = new List<string>();
        var exclude = new List<string>();
        string? outDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--help" or "-h") { PrintUsage(); return 0; }
            else if (a == "--include" && i + 1 < args.Length) include.Add(args[++i]);
            else if (a.StartsWith("--include=")) include.Add(a["--include=".Length..]);
            else if (a == "--exclude" && i + 1 < args.Length) exclude.Add(args[++i]);
            else if (a.StartsWith("--exclude=")) exclude.Add(a["--exclude=".Length..]);
            else if (a == "--out" && i + 1 < args.Length) outDir = args[++i];
            else if (a.StartsWith("--out=")) outDir = a["--out=".Length..];
            else if (a.StartsWith('-')) { Console.Error.WriteLine($"imp migrate: unknown flag '{a}'"); return 1; }
            else explicitPaths.Add(a);
        }

        // ── repo + substrate sanity ─────────────────────────────
        var repoRoot = Signals.ResolveRepoRoot(Directory.GetCurrentDirectory());
        if (repoRoot is null)
        {
            Console.Error.WriteLine("imp migrate: not in a git repo");
            return 1;
        }
        if (!Directory.Exists(System.IO.Path.Combine(repoRoot, "imp"))
            || !File.Exists(System.IO.Path.Combine(repoRoot, "imp", "_meta", "conventions.md")))
        {
            // Per spec: substrate must already exist. Soft-warn and continue
            // — the plan output is still useful even without substrate, and
            // forcing init first would block dry-run inspection.
            Console.Error.WriteLine("imp migrate: warning — no substrate detected at imp/. Run `imp init` to create one before applying proposals.");
        }

        // ── discover sources ────────────────────────────────────
        var (sources, docs) = DiscoverDocs(repoRoot, explicitPaths, include, exclude);
        if (docs.Count == 0)
        {
            Console.Error.WriteLine("imp migrate: no docs to migrate.");
            if (explicitPaths.Count == 0)
                Console.Error.WriteLine("  (auto-discovery looks at project/. Pass explicit paths to override.)");
            return 1;
        }

        Console.Error.WriteLine($"[imp] migrate: discovered {docs.Count} doc(s) from {sources.Count} source(s)");

        // ── per-doc signals + sniff ─────────────────────────────
        var records = new List<DocRecord>();
        foreach (var relPath in docs)
        {
            string content;
            try { content = File.ReadAllText(System.IO.Path.Combine(repoRoot, relPath)); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  skip {relPath}: read failed: {ex.Message}");
                continue;
            }
            var signals = Signals.Gather(repoRoot, relPath, content);
            var (shape, reason) = Sniff(signals, content);
            var action = ProposedActionFor(shape, signals);
            records.Add(new DocRecord(relPath, shape, reason, action, signals));
        }

        // ── plan output ─────────────────────────────────────────
        var startedAt = DateTimeOffset.UtcNow;
        var migrationId = $"M-{startedAt:yyyy-MM-dd-HHmm}";
        var resolvedOut = outDir ?? DefaultOutDir(repoRoot, migrationId);
        Directory.CreateDirectory(resolvedOut);

        var plan = new MigrationPlan(
            MigrationId: migrationId,
            StartedAt: startedAt.ToString("o"),
            RepoRoot: repoRoot,
            Sources: sources,
            Docs: records);

        var jsonPath = System.IO.Path.Combine(resolvedOut, "plan.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(plan, JsonOpts));

        var mdPath = System.IO.Path.Combine(resolvedOut, "plan.md");
        File.WriteAllText(mdPath, RenderPlanMarkdown(plan));

        Console.WriteLine($"imp migrate: plan written to {RelativeTo(repoRoot, resolvedOut)}/");
        Console.WriteLine($"  {records.Count} docs / {records.Sum(r => r.Signals.Bytes)} bytes");
        Console.WriteLine($"  shapes: {string.Join(", ", ShapeBreakdown(records))}");
        Console.WriteLine();
        Console.WriteLine($"Next: review {RelativeTo(repoRoot, mdPath)}.");
        Console.WriteLine($"Phase 2 (cloud classification + proposal drafting) is not built yet — see plans/project-migrate-phase1.md.");

        return 0;
    }

    static void PrintUsage()
    {
        Console.WriteLine("usage: imp migrate [paths...] [--include glob] [--exclude glob] [--out dir]");
        Console.WriteLine();
        Console.WriteLine("  paths              explicit markdown files or directories to consider");
        Console.WriteLine("                     (default: auto-discover from project/)");
        Console.WriteLine("  --include <glob>   only docs matching glob (relative path)");
        Console.WriteLine("  --exclude <glob>   skip docs matching glob (relative path)");
        Console.WriteLine("  --out <dir>        write plan.md / plan.json here");
        Console.WriteLine("                     (default: <repo>.imp-proposals/_migration/M-<timestamp>/)");
        Console.WriteLine();
        Console.WriteLine("Phase 1 only — discovery, signals, heuristic shape sniff, plan output.");
        Console.WriteLine("No model calls. Phase 2 (classification, proposals) is not built yet.");
    }

    // ── discovery ──────────────────────────────────────────────────

    static (List<string> Sources, List<string> Docs) DiscoverDocs(
        string repoRoot,
        List<string> explicitPaths,
        List<string> include,
        List<string> exclude)
    {
        var sources = new List<string>();
        var docSet = new SortedSet<string>(StringComparer.Ordinal);

        if (explicitPaths.Count > 0)
        {
            foreach (var p in explicitPaths)
            {
                var abs = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(repoRoot, p));
                var rel = System.IO.Path.GetRelativePath(repoRoot, abs).Replace(System.IO.Path.DirectorySeparatorChar, '/');
                if (Directory.Exists(abs))
                {
                    sources.Add(rel + "/");
                    foreach (var f in EnumerateMarkdown(abs)) docSet.Add(ToRel(repoRoot, f));
                }
                else if (File.Exists(abs) && abs.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    sources.Add(rel);
                    docSet.Add(rel);
                }
                else
                {
                    Console.Error.WriteLine($"  skip {p}: not a markdown file or directory");
                }
            }
        }
        else
        {
            // v0.1 auto-discovery: project/ only, if it exists and isn't substrate-shaped.
            var projectDir = System.IO.Path.Combine(repoRoot, "project");
            if (Directory.Exists(projectDir))
            {
                if (File.Exists(System.IO.Path.Combine(projectDir, "_meta", "conventions.md")))
                {
                    Console.Error.WriteLine("  skip project/: looks substrate-shaped (has _meta/conventions.md)");
                }
                else
                {
                    sources.Add("project/");
                    foreach (var f in EnumerateMarkdown(projectDir)) docSet.Add(ToRel(repoRoot, f));
                }
            }
        }

        // Apply --include / --exclude after discovery.
        var filtered = docSet
            .Where(d => include.Count == 0 || include.Any(g => MatchesGlob(d, g)))
            .Where(d => !exclude.Any(g => MatchesGlob(d, g)))
            .ToList();

        return (sources, filtered);
    }

    static IEnumerable<string> EnumerateMarkdown(string dir)
    {
        // Skip nested substrate dirs and the imp-proposals dump.
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".imp", "node_modules", "bin", "obj",
        };
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(current); }
            catch { continue; }
            foreach (var e in entries)
            {
                var name = System.IO.Path.GetFileName(e);
                if (Directory.Exists(e))
                {
                    if (skip.Contains(name)) continue;
                    stack.Push(e);
                }
                else if (e.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    yield return e;
                }
            }
        }
    }

    static string ToRel(string repoRoot, string abs) =>
        System.IO.Path.GetRelativePath(repoRoot, abs).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    // Filename-glob match against a relative path. `*` matches any chars
    // except `/`; `**` matches across path separators. Anchored at start
    // of the relative path.
    static bool MatchesGlob(string relPath, string glob)
    {
        var pattern = "^" + Regex.Escape(glob)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(relPath, pattern);
    }

    // ── sniff ──────────────────────────────────────────────────────

    // First-match-wins. Returns shape + the rule that fired (for plan.md).
    public static (DocShape Shape, string Reason) Sniff(SignalsReport s, string content)
    {
        var lines = content.Split('\n');
        var nonBlank = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        // 1. Task list — primary content is checklist items.
        var checklistCount = lines.Count(l => Regex.IsMatch(l, @"^\s*-\s*\[[ xX]\]"));
        if (nonBlank.Length > 0 && checklistCount * 2 >= nonBlank.Length)
            return (DocShape.TaskList, $"{checklistCount} checklist items / {nonBlank.Length} non-blank lines");

        // 2. Reference — external system docs.
        var title = ExtractTitle(content);
        var titleLower = (title ?? "").ToLowerInvariant();
        if (titleLower.Contains("api ") || titleLower.Contains(" api") || titleLower.Contains("reference") || titleLower.Contains("docs"))
            return (DocShape.Reference, $"title '{title}' looks reference-shaped");
        var urlCount = Regex.Matches(content, @"https?://").Count;
        if (s.Lines > 20 && urlCount * 20 > s.Lines && s.CodeRefs.LiveCount <= 1)
            return (DocShape.Reference, $"{urlCount} URLs over {s.Lines} lines, near-zero code refs");

        // 3. Plan-shaped — single coherent design / spec / plan.
        // H1 >= 1 (not == 1) because Signals' fenced-block-stripping still
        // counts H1 lines outside fences; a doc with one real H1 plus an
        // example block can show H1=2.
        if (s.SelfLabels.HasFrontmatter || s.Structure.H1 >= 1)
        {
            var planLikeTitle = titleLower.Contains("plan")
                || titleLower.Contains("spec")
                || titleLower.Contains("design")
                || titleLower.Contains("proposal")
                || titleLower.Contains("brief")
                || titleLower.Contains("research")
                || titleLower.Contains("idea")
                || titleLower.Contains("roadmap");
            if (planLikeTitle && s.Structure.H2 <= 10)
                return (DocShape.PlanShaped, $"H1 + ≤10 H2 + plan-flavored title '{title}'");
            if (s.SelfLabels.HasFrontmatter && s.Structure.H2 <= 8)
                return (DocShape.PlanShaped, $"frontmatter + ≤8 H2");
        }

        // 4. Mixed — many H2 sections, likely covering different kinds.
        if (s.Structure.H2 >= 10)
            return (DocShape.Mixed, $"{s.Structure.H2} H2 sections — probably crosses kinds");

        // 5. Flavor — prose-heavy, no code references.
        if (s.CodeRefs.LiveCount == 0 && s.CodeRefs.AbsentCount == 0 && s.Structure.CodeBlocks <= 1 && s.Lines >= 30)
            return (DocShape.Flavor, "prose-only, no code references, no fenced blocks");

        // 6. Fallthrough.
        return (DocShape.Unknown, "no rule fired confidently");
    }

    static string? ExtractTitle(string content)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("# ")) return line[2..].Trim();
        }
        return null;
    }

    // ── proposed action ────────────────────────────────────────────

    // First-cut deterministic mapping from sniff shape → suggested
    // substrate destination. Phase 2 will refine this with model judgment.
    static string ProposedActionFor(DocShape shape, SignalsReport s) => shape switch
    {
        DocShape.PlanShaped => "migrate-to-plans",
        DocShape.Reference => "migrate-to-reference",
        DocShape.TaskList => "merge-into-TODO",
        DocShape.Flavor => "migrate-to-learnings (as aspiration / context)",
        DocShape.Mixed => "split-and-classify (Phase 2)",
        DocShape.Unknown => "defer (Phase 2 / human)",
        _ => "defer",
    };

    // ── plan.md rendering ──────────────────────────────────────────

    static string RenderPlanMarkdown(MigrationPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Migration plan {plan.MigrationId}");
        sb.AppendLine();
        sb.AppendLine($"_Generated {plan.StartedAt}. Phase 1 only — heuristic, no model calls._");
        sb.AppendLine();

        // Summary
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- **Docs:** {plan.Docs.Count}, {plan.Docs.Sum(d => d.Signals.Bytes):N0} bytes total");
        sb.AppendLine($"- **Sources:** {string.Join(", ", plan.Sources)}");
        sb.AppendLine($"- **Shape breakdown:** {string.Join(", ", ShapeBreakdown(plan.Docs))}");
        sb.AppendLine();

        // Per-doc table
        sb.AppendLine("## Per-doc table");
        sb.AppendLine();
        sb.AppendLine("| Path | Shape | Last touched | Lines | In/Out refs | Action |");
        sb.AppendLine("|---|---|---:|---:|---:|---|");
        foreach (var d in plan.Docs.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            var last = ShortDate(d.Signals.Git.LastModified) ?? "—";
            sb.AppendLine($"| `{d.Path}` | {d.Shape} | {last} | {d.Signals.Lines} | {d.Signals.CrossRefs.Incoming.Count}/{d.Signals.CrossRefs.Outgoing.Count} | {d.ProposedAction} |");
        }
        sb.AppendLine();

        // Detail
        sb.AppendLine("## Detail");
        sb.AppendLine();
        foreach (var d in plan.Docs.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            sb.AppendLine($"### `{d.Path}`");
            sb.AppendLine();
            sb.AppendLine($"- **shape:** {d.Shape} — {d.SniffReason}");
            sb.AppendLine($"- **proposed action:** {d.ProposedAction}");
            sb.AppendLine($"- **size:** {d.Signals.Bytes:N0} bytes / {d.Signals.Lines} lines");
            sb.AppendLine($"- **git:** last {ShortDate(d.Signals.Git.LastModified) ?? "—"}, first {ShortDate(d.Signals.Git.FirstSeen) ?? "—"}, {d.Signals.Git.CommitsTouching} commits");
            sb.AppendLine($"- **structure:** H1={d.Signals.Structure.H1}, H2={d.Signals.Structure.H2}, H3+={d.Signals.Structure.H3Plus}, code blocks={d.Signals.Structure.CodeBlocks}");
            sb.AppendLine($"- **self-labels:** frontmatter={(d.Signals.SelfLabels.HasFrontmatter ? "yes" : "no")}, status={d.Signals.SelfLabels.StatusLine ?? "—"}, DECIDED={d.Signals.SelfLabels.DecidedCount}");
            sb.AppendLine($"- **cross-refs:** {d.Signals.CrossRefs.Incoming.Count} incoming, {d.Signals.CrossRefs.Outgoing.Count} outgoing");
            sb.AppendLine($"- **code-refs:** live={d.Signals.CodeRefs.LiveCount}, absent={d.Signals.CodeRefs.AbsentCount} (of {d.Signals.CodeRefs.CandidatesExtracted} candidates)");
            if (d.Signals.CodeRefs.LiveSample.Count > 0)
                sb.AppendLine($"  - live sample: {string.Join(", ", d.Signals.CodeRefs.LiveSample.Take(8))}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("_Phase 2 (cloud-subagent classification + proposal drafting) is not built yet. Treat the proposed actions above as first-cut suggestions; the human reviewer is the source of truth until Phase 2 lands._");
        return sb.ToString();
    }

    static IEnumerable<string> ShapeBreakdown(IEnumerable<DocRecord> docs) =>
        docs.GroupBy(d => d.Shape)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}={g.Count()}");

    static string? ShortDate(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        return DateTimeOffset.TryParse(iso, out var dt) ? dt.ToString("yyyy-MM-dd") : iso;
    }

    // ── output dir ─────────────────────────────────────────────────

    // Sibling-of-repo placement matches WikiArchive / ResearchArchive.
    static string DefaultOutDir(string repoRoot, string migrationId)
    {
        var absRoot = System.IO.Path.GetFullPath(repoRoot);
        var name = System.IO.Path.GetFileName(absRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        var parent = System.IO.Path.GetDirectoryName(absRoot)
            ?? throw new InvalidOperationException($"Repo root '{absRoot}' has no parent directory.");
        return System.IO.Path.Combine(parent, $"{name}.imp-proposals", "_migration", migrationId);
    }

    static string RelativeTo(string baseDir, string fullPath)
    {
        var rel = System.IO.Path.GetRelativePath(baseDir, fullPath);
        return rel.Replace(System.IO.Path.DirectorySeparatorChar, '/');
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Snake-case for everything; explicit JsonPropertyName attributes still win.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
