using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Imp.Substrate;

// `imp signals <path> [--json]` — gathers per-doc signals for migration
// classification: git dates, line/heading counts, self-labels (Status: /
// frontmatter / DECIDED markers), cross-references (outgoing + incoming),
// code-reference presence (PascalCase terms in current code vs. absent).
//
// Pure mechanical pass. No model calls. Intended as the cheap-phase
// primitive that /project-migrate consumes per-doc to inform
// classification.
public static class Signals
{
    public static int Run(string[] args)
    {
        bool json = false;
        string? path = null;
        foreach (var a in args)
        {
            if (a is "--json") json = true;
            else if (a is "--help" or "-h") { PrintUsage(); return 0; }
            else if (a.StartsWith('-')) { Console.Error.WriteLine($"imp signals: unknown flag '{a}'"); return 1; }
            else if (path is null) path = a;
            else { Console.Error.WriteLine("imp signals: too many positional arguments"); return 1; }
        }

        if (path is null) { PrintUsage(); return 1; }
        if (!File.Exists(path)) { Console.Error.WriteLine($"imp signals: file not found: {path}"); return 1; }

        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath) ?? ".";
        var repoRoot = GitRepoRoot(dir);
        if (repoRoot is null)
        {
            Console.Error.WriteLine("imp signals: not in a git repo");
            return 1;
        }
        var relPath = Path.GetRelativePath(repoRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        var content = File.ReadAllText(fullPath);

        var report = new SignalsReport(
            Doc: relPath,
            Bytes: content.Length,
            Lines: content.Count(c => c == '\n') + 1,
            Git: GitSignals(repoRoot, relPath),
            Structure: ExtractStructure(content),
            SelfLabels: ExtractSelfLabels(content),
            CrossRefs: ExtractCrossRefs(content, repoRoot, relPath),
            CodeRefs: ExtractCodeRefs(content, repoRoot)
        );

        if (json)
        {
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            Console.WriteLine(JsonSerializer.Serialize(report, opts));
        }
        else
        {
            PrintHumanReadable(report);
        }
        return 0;
    }

    static void PrintUsage()
    {
        Console.WriteLine("usage: imp signals <path> [--json]");
        Console.WriteLine();
        Console.WriteLine("  path    path to a markdown file inside a git repo");
        Console.WriteLine("  --json  emit machine-readable JSON; default is human-readable");
    }

    static void PrintHumanReadable(SignalsReport r)
    {
        Console.WriteLine($"signals for {r.Doc}");
        Console.WriteLine($"  size: {r.Bytes} bytes / {r.Lines} lines");
        Console.WriteLine();
        Console.WriteLine("  git:");
        Console.WriteLine($"    last_modified : {r.Git.LastModified ?? "(none)"}");
        Console.WriteLine($"    first_seen    : {r.Git.FirstSeen ?? "(none)"}");
        Console.WriteLine($"    commits       : {r.Git.CommitsTouching}");
        Console.WriteLine();
        Console.WriteLine("  structure:");
        Console.WriteLine($"    h1 / h2 / h3+ : {r.Structure.H1} / {r.Structure.H2} / {r.Structure.H3Plus}");
        Console.WriteLine($"    code blocks   : {r.Structure.CodeBlocks}");
        Console.WriteLine($"    list items    : {r.Structure.ListItems}");
        Console.WriteLine();
        Console.WriteLine("  self-labels:");
        Console.WriteLine($"    frontmatter   : {(r.SelfLabels.HasFrontmatter ? "yes" : "no")}");
        Console.WriteLine($"    status line   : {r.SelfLabels.StatusLine ?? "(none)"}");
        Console.WriteLine($"    DECIDED count : {r.SelfLabels.DecidedCount}");
        Console.WriteLine();
        Console.WriteLine("  cross-refs:");
        Console.WriteLine($"    outgoing      : {r.CrossRefs.Outgoing.Count} → {(r.CrossRefs.Outgoing.Count > 0 ? string.Join(", ", r.CrossRefs.Outgoing.Take(5)) + (r.CrossRefs.Outgoing.Count > 5 ? ", ..." : "") : "(none)")}");
        Console.WriteLine($"    incoming      : {r.CrossRefs.Incoming.Count} ← {(r.CrossRefs.Incoming.Count > 0 ? string.Join(", ", r.CrossRefs.Incoming.Take(5)) + (r.CrossRefs.Incoming.Count > 5 ? ", ..." : "") : "(none)")}");
        Console.WriteLine();
        Console.WriteLine("  code-refs:");
        Console.WriteLine($"    candidates    : {r.CodeRefs.CandidatesExtracted} terms extracted");
        Console.WriteLine($"    live          : {r.CodeRefs.LiveCount} ({string.Join(", ", r.CodeRefs.LiveSample.Take(8))}{(r.CodeRefs.LiveCount > 8 ? ", ..." : "")})");
        Console.WriteLine($"    absent        : {r.CodeRefs.AbsentCount} ({string.Join(", ", r.CodeRefs.AbsentSample.Take(8))}{(r.CodeRefs.AbsentCount > 8 ? ", ..." : "")})");
    }

    // ── git ───────────────────────────────────────────────────────

    static string? GitRepoRoot(string startDir)
    {
        var (ok, stdout) = RunGit(startDir, "rev-parse", "--show-toplevel");
        return ok ? stdout.Trim() : null;
    }

    static GitInfo GitSignals(string repoRoot, string relPath)
    {
        var (okLast, lastOut) = RunGit(repoRoot, "log", "-1", "--format=%aI", "--", relPath);
        var (okFirst, firstOut) = RunGit(repoRoot, "log", "--follow", "--format=%aI", "--", relPath);
        var (okCount, countOut) = RunGit(repoRoot, "log", "--follow", "--format=%H", "--", relPath);

        var lastModified = okLast && lastOut.Trim().Length > 0 ? lastOut.Trim() : null;

        string? firstSeen = null;
        if (okFirst)
        {
            var lines = firstOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            firstSeen = lines.Length > 0 ? lines[^1].Trim() : null;
        }

        int commits = 0;
        if (okCount)
        {
            commits = countOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        return new GitInfo(lastModified, firstSeen, commits);
    }

    static (bool Ok, string Output) RunGit(string cwd, params string[] args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = cwd,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(15_000))
            {
                try { proc.Kill(true); } catch { }
                return (false, "");
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            return (proc.ExitCode == 0, stdout);
        }
        catch
        {
            return (false, "");
        }
    }

    // ── structure ─────────────────────────────────────────────────

    static StructureInfo ExtractStructure(string content)
    {
        int h1 = 0, h2 = 0, h3plus = 0, code = 0, list = 0;
        bool inCode = false;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("```") || line.StartsWith("~~~"))
            {
                inCode = !inCode;
                if (inCode) code++;
                continue;
            }
            if (inCode) continue;
            if (line.StartsWith("# ") && !line.StartsWith("## ")) h1++;
            else if (line.StartsWith("## ") && !line.StartsWith("### ")) h2++;
            else if (line.StartsWith("### ")) h3plus++;
            else if (Regex.IsMatch(line, @"^\s*(-|\*|\d+\.)\s")) list++;
        }
        return new StructureInfo(h1, h2, h3plus, code, list);
    }

    // ── self-labels ───────────────────────────────────────────────

    static SelfLabelsInfo ExtractSelfLabels(string content)
    {
        var hasFrontmatter = content.StartsWith("---\n") || content.StartsWith("---\r\n");

        // Strip markdown emphasis markers per line before matching, so
        // "**Status:** Implemented" matches the same as "Status: Implemented".
        string? statusLine = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = Regex.Replace(raw, @"[*_]", "");
            var m = Regex.Match(line, @"^\s*Status\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
            if (m.Success) { statusLine = m.Groups[1].Value.Trim(); break; }
        }

        var decidedCount = Regex.Matches(content, @"\bDECIDED\b").Count;
        return new SelfLabelsInfo(hasFrontmatter, statusLine, decidedCount);
    }

    // ── cross-refs ────────────────────────────────────────────────

    static CrossRefsInfo ExtractCrossRefs(string content, string repoRoot, string relPath)
    {
        // Outgoing: markdown links to .md files in this content.
        var outgoing = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(content, @"\]\(([^)]+\.md)(?:[^)]*)\)"))
        {
            var target = m.Groups[1].Value;
            if (target.StartsWith("http")) continue;
            outgoing.Add(target);
        }

        // Incoming: any tracked .md file mentioning this doc's basename. Bounded by git grep.
        var incoming = new List<string>();
        var basename = Path.GetFileName(relPath);
        var (ok, stdout) = RunGit(repoRoot, "grep", "-l", "-F", basename, "--", "*.md");
        if (ok)
        {
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Trim();
                if (p == relPath) continue;     // self
                if (incoming.Count >= 100) break;
                incoming.Add(p);
            }
        }

        return new CrossRefsInfo(outgoing.ToList(), incoming);
    }

    // ── code-refs ─────────────────────────────────────────────────

    static CodeRefsInfo ExtractCodeRefs(string content, string repoRoot)
    {
        // Two passes against the doc for "code thing" signals.
        //
        // TODO: replace both with AST-driven extraction once that work
        // lands. Both passes here are heuristic stopgaps:
        //
        //   1. PascalCase identifiers in prose — too narrow (drops
        //      single-word names like Shell), strips fenced blocks
        //      (loses filename references), and the CommonWords skip
        //      list is hand-curated and fragile.
        //   2. Filenames in the full doc body — vague proxy for "this
        //      doc references a code thing". Catches things the
        //      PascalCase pass misses (e.g. "Shell/BashTool.cs"
        //      anywhere in the doc), but doesn't distinguish e.g. an
        //      old code path mention from a current one. Whole-file
        //      list comes from `git ls-files` (basenames only).

        var prose = StripFencedBlocks(content);

        // Pass 1: PascalCase from prose.
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(prose, @"\b[A-Z][a-z]+(?:[A-Z][a-z]+)+\b"))
        {
            var t = m.Value;
            if (t.Length < 5) continue;
            if (CommonWords.Contains(t)) continue;
            candidates.Add(t);
        }

        // Pass 2: filenames anywhere in the full doc body.
        var fileExtensions = new[] { "cs", "ts", "tsx", "js", "jsx", "py", "go", "rs", "java", "yaml", "yml", "json", "toml", "csproj", "html" };
        var extPattern = string.Join("|", fileExtensions);
        var filenameCandidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(content, $@"\b[\w-]+\.(?:{extPattern})\b"))
        {
            filenameCandidates.Add(m.Value);
        }

        // Resolve filenames against the actual filesystem (basename match).
        // Walks the repo, skipping common build / vcs dirs, so gitignored
        // but present files (fake-tools.yaml, .nb_conversation_history.json)
        // count as live rather than absent.
        var trackedBasenames = CollectRepoBasenames(repoRoot);

        var live = new List<string>();
        var absent = new List<string>();

        foreach (var term in candidates.OrderBy(s => s, StringComparer.Ordinal))
        {
            var (ok, stdout) = RunGit(repoRoot, "grep", "-l", "-w", "-F", term, "--",
                "*.cs", "*.ts", "*.tsx", "*.js", "*.jsx", "*.py", "*.go", "*.rs", "*.java");
            if (ok && stdout.Trim().Length > 0) live.Add(term);
            else absent.Add(term);
        }

        foreach (var fname in filenameCandidates.OrderBy(s => s, StringComparer.Ordinal))
        {
            if (trackedBasenames.Contains(fname)) live.Add(fname);
            else absent.Add(fname);
        }

        return new CodeRefsInfo(
            CandidatesExtracted: candidates.Count + filenameCandidates.Count,
            LiveCount: live.Count,
            AbsentCount: absent.Count,
            LiveSample: live.Take(20).ToList(),
            AbsentSample: absent.Take(20).ToList()
        );
    }

    static HashSet<string> CollectRepoBasenames(string repoRoot)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal)
        {
            ".git", ".imp", "bin", "obj", "node_modules", ".idea", ".vs", ".vscode", ".next", "dist", "target",
        };
        var basenames = new HashSet<string>(StringComparer.Ordinal);
        WalkInto(repoRoot, basenames, skip);
        return basenames;
    }

    static void WalkInto(string dir, HashSet<string> sink, HashSet<string> skipDirNames)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (skipDirNames.Contains(name)) continue;
                WalkInto(entry, sink, skipDirNames);
            }
            else
            {
                sink.Add(name);
            }
        }
    }

    static string StripFencedBlocks(string content)
    {
        var sb = new System.Text.StringBuilder();
        bool inCode = false;
        foreach (var line in content.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~"))
            {
                inCode = !inCode;
                continue;
            }
            if (!inCode) sb.AppendLine(line);
        }
        return sb.ToString();
    }

    // Conservative skip list — common English words that are also PascalCase
    // and would create noise in code-grep. Curated; will need extension.
    static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "Project", "Configuration", "Architecture", "Manager", "System", "Provider",
        "Service", "Controller", "Handler", "Factory", "Builder", "Loader",
        "Reader", "Writer", "Stream", "String", "Object", "Method", "Property",
        "Description", "Implementation", "Phase", "Status", "Decision", "Decisions",
        "Reference", "References", "Note", "Notes", "Example", "Examples",
        "Overview", "Summary", "Detail", "Details", "Section", "Sections",
        "Default", "Optional", "Required", "Standard", "Custom",
    };
}

// ── records ────────────────────────────────────────────────

record SignalsReport(
    string Doc,
    int Bytes,
    int Lines,
    GitInfo Git,
    StructureInfo Structure,
    SelfLabelsInfo SelfLabels,
    CrossRefsInfo CrossRefs,
    CodeRefsInfo CodeRefs
);

record GitInfo(
    [property: JsonPropertyName("last_modified")] string? LastModified,
    [property: JsonPropertyName("first_seen")] string? FirstSeen,
    [property: JsonPropertyName("commits_touching")] int CommitsTouching
);

record StructureInfo(
    int H1,
    int H2,
    [property: JsonPropertyName("h3_plus")] int H3Plus,
    [property: JsonPropertyName("code_blocks")] int CodeBlocks,
    [property: JsonPropertyName("list_items")] int ListItems
);

record SelfLabelsInfo(
    [property: JsonPropertyName("has_frontmatter")] bool HasFrontmatter,
    [property: JsonPropertyName("status_line")] string? StatusLine,
    [property: JsonPropertyName("decided_count")] int DecidedCount
);

record CrossRefsInfo(
    List<string> Outgoing,
    List<string> Incoming
);

record CodeRefsInfo(
    [property: JsonPropertyName("candidates_extracted")] int CandidatesExtracted,
    [property: JsonPropertyName("live_count")] int LiveCount,
    [property: JsonPropertyName("absent_count")] int AbsentCount,
    [property: JsonPropertyName("live_sample")] List<string> LiveSample,
    [property: JsonPropertyName("absent_sample")] List<string> AbsentSample
);
