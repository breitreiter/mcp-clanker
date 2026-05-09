using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Imp.Wiki;

// Step 3 of project/wiki-plan.md: walk a repo subtree, decide per-directory
// whether `imp wiki` should SKIP / STUB / RUN, and emit a plan. No dispatch
// here — that's step 4 (Wiki.RunAsync). `imp wiki --dry-run` calls Plan and
// prints the result.
//
// Enumeration is tracked-only via `git ls-tree HEAD:<path>`. That naturally
// excludes .git, gitignored build outputs, and untracked WIP. The latter is
// a known v0 gap (the design sketch flags a content-hash fallback as future
// work); flagging worktree_dirty in frontmatter lands with the renderer.
//
// Per-page source_tree_sha is sha256 of the raw ls-tree bytes for the dir.
// ls-tree includes subdirectory entries as their tree-object hashes, which
// change recursively when anything below mutates — so the SHA flips on any
// nested change without us walking the subtree ourselves.

public enum WikiDecision
{
    Run,    // dispatch a research run for this directory
    Skip,   // existing wiki page's source_tree_sha matches; nothing to do
    Stub,   // total source bytes exceed Wiki:MaxDirBytes; write/refresh stub
}

public sealed record WikiTarget(
    [property: JsonPropertyName("source_path")] string RelativePath,   // "" for repo root
    [property: JsonPropertyName("page_path")] string PagePath,         // wiki/<...>.md, repo-relative
    [property: JsonPropertyName("decision")] WikiDecision Decision,
    [property: JsonPropertyName("source_tree_sha")] string SourceTreeSha,
    [property: JsonPropertyName("source_bytes")] long SourceBytes,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record WikiPlan(
    [property: JsonPropertyName("repo_root")] string RepoRoot,
    [property: JsonPropertyName("wiki_dir")] string WikiDir,
    [property: JsonPropertyName("max_dir_bytes")] long MaxDirBytes,
    [property: JsonPropertyName("targets")] IReadOnlyList<WikiTarget> Targets,
    [property: JsonPropertyName("summary")] WikiPlanSummary Summary);

public sealed record WikiPlanSummary(
    [property: JsonPropertyName("run")] int Run,
    [property: JsonPropertyName("skip")] int Skip,
    [property: JsonPropertyName("stub")] int Stub);

public static class WikiPlanner
{
    // Text-like extensions that count toward source_bytes and file_count.
    // Anything not on this list is treated as binary/generated for budget
    // purposes — the executor still won't read it (it's not in the brief),
    // but it doesn't push the dir over the threshold either.
    static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".md", ".json", ".yaml", ".yml", ".ts", ".tsx", ".js", ".jsx",
        ".py", ".go", ".rs", ".sh", ".toml", ".ini", ".xml", ".html", ".css",
        ".sql", ".rb", ".java", ".kt", ".swift", ".c", ".cpp", ".h", ".hpp",
        ".csproj", ".sln", ".props", ".targets",
    };

    // Always-skipped subdirectory names. Most are also gitignored in any sane
    // setup, but the explicit list defends against repos that track their bin/
    // or vendor dirs. Wiki output dir is filtered separately (it's configurable).
    static readonly HashSet<string> IgnoredDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".imp", "node_modules", "bin", "obj", ".idea", ".vs", ".vscode",
    };

    public static WikiPlan Plan(string repoRoot, string targetSubpath, string wikiDir, long maxDirBytes)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        targetSubpath = NormalizeRelative(targetSubpath);
        wikiDir = NormalizeRelative(wikiDir);
        if (string.IsNullOrEmpty(wikiDir)) wikiDir = "imp-wiki";

        var targets = new List<WikiTarget>();
        WalkDirectory(repoRoot, targetSubpath, wikiDir, maxDirBytes, targets);

        var summary = new WikiPlanSummary(
            Run: targets.Count(t => t.Decision == WikiDecision.Run),
            Skip: targets.Count(t => t.Decision == WikiDecision.Skip),
            Stub: targets.Count(t => t.Decision == WikiDecision.Stub));

        return new WikiPlan(repoRoot, wikiDir, maxDirBytes, targets, summary);
    }

    public static string SerializePlan(WikiPlan plan) => JsonSerializer.Serialize(plan, JsonOpts);

    // Direct allowlisted-extension files in a target directory, in
    // ls-tree order. Used by the orchestrator to populate SuggestedSources
    // on a per-target brief. Repo-relative paths.
    public static IReadOnlyList<string> EnumerateSourceFiles(string repoRoot, string relativePath)
    {
        relativePath = NormalizeRelative(relativePath);
        var (ok, raw) = RunGitLsTree(repoRoot, relativePath);
        if (!ok) return Array.Empty<string>();

        var files = new List<string>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var meta = line[..tab];
            var name = line[(tab + 1)..];
            var parts = meta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[1] != "blob") continue;
            var ext = Path.GetExtension(name);
            if (!SourceExtensions.Contains(ext)) continue;
            files.Add(relativePath.Length == 0 ? name : $"{relativePath}/{name}");
        }
        return files;
    }

    // Map a source-tree relative path to its wiki page path. Repo root maps to
    // <wikiDir>/README.md (which doubles as the index). Any other dir maps to
    // <wikiDir>/<relative>.md — so src/Foo/ → wiki/src/Foo.md.
    public static string PageRelativePathFor(string sourceRelativePath, string wikiDir)
    {
        sourceRelativePath = NormalizeRelative(sourceRelativePath);
        wikiDir = NormalizeRelative(wikiDir);
        var path = sourceRelativePath.Length == 0
            ? Path.Combine(wikiDir, "README.md")
            : Path.Combine(wikiDir, sourceRelativePath + ".md");
        return path.Replace('\\', '/');
    }

    static void WalkDirectory(
        string repoRoot,
        string relativePath,
        string wikiDir,
        long maxDirBytes,
        List<WikiTarget> output)
    {
        if (PathStartsWith(relativePath, wikiDir)) return;

        var (ok, raw) = RunGitLsTree(repoRoot, relativePath);
        if (!ok) return;

        long sourceBytes = 0;
        int fileCount = 0;
        var subdirs = new List<string>();

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // ls-tree --long: "<mode> <type> <hash> <size>\t<name>"
            // size is "      -" (right-padded) for trees, an integer for blobs.
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var meta = line[..tab];
            var name = line[(tab + 1)..];
            var parts = meta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            var type = parts[1];

            if (type == "tree")
            {
                if (IgnoredDirNames.Contains(name)) continue;
                var childRel = relativePath.Length == 0 ? name : $"{relativePath}/{name}";
                if (PathEquals(childRel, wikiDir)) continue;
                subdirs.Add(childRel);
            }
            else if (type == "blob")
            {
                var ext = Path.GetExtension(name);
                if (!SourceExtensions.Contains(ext)) continue;
                fileCount++;
                if (parts.Length >= 4 && long.TryParse(parts[3], out var size))
                    sourceBytes += size;
            }
        }

        var sha = ShaHex(raw);

        // Repo-root targets collide with wiki/README.md (the index slot) and
        // adaptive splitting / repo-level synthesis is post-v0. Skip emitting
        // the root as a target; the index renderer covers the README slot.
        if (fileCount > 0 && relativePath.Length > 0)
        {
            var pagePath = PageRelativePathFor(relativePath, wikiDir);
            var pageAbs = Path.Combine(repoRoot, pagePath);
            var (pageSha, pageStatus) = ReadPageFrontmatter(pageAbs);

            WikiDecision decision;
            string reason;
            var isOversizedStub = string.Equals(pageStatus, "oversized", StringComparison.OrdinalIgnoreCase);

            if (sourceBytes > maxDirBytes)
            {
                decision = WikiDecision.Stub;
                reason = $"source bytes {sourceBytes} > MaxDirBytes {maxDirBytes}";
            }
            else if (pageSha == sha && !isOversizedStub)
            {
                decision = WikiDecision.Skip;
                reason = "source_tree_sha matches existing page";
            }
            else
            {
                decision = WikiDecision.Run;
                reason = pageSha is null
                    ? "no existing page"
                    : isOversizedStub
                        ? "previous run wrote oversized stub; re-evaluating"
                        : "source_tree_sha mismatch";
            }

            output.Add(new WikiTarget(
                RelativePath: relativePath,
                PagePath: pagePath,
                Decision: decision,
                SourceTreeSha: sha,
                SourceBytes: sourceBytes,
                FileCount: fileCount,
                Reason: reason));
        }

        foreach (var sub in subdirs)
            WalkDirectory(repoRoot, sub, wikiDir, maxDirBytes, output);
    }

    static (bool Ok, string Output) RunGitLsTree(string repoRoot, string relativePath)
    {
        var treeRef = relativePath.Length == 0 ? "HEAD" : $"HEAD:{relativePath}";
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.StartInfo.ArgumentList.Add("ls-tree");
            proc.StartInfo.ArgumentList.Add("--long");
            proc.StartInfo.ArgumentList.Add(treeRef);

            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
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

    static (string? Sha, string? Status) ReadPageFrontmatter(string pagePath)
    {
        var fm = WikiPageFrontmatterReader.Parse(pagePath);
        return (fm?.SourceTreeSha, fm?.Status);
    }

    static string NormalizeRelative(string p) =>
        (p ?? "").Replace('\\', '/').Trim('/');

    static bool PathEquals(string a, string b) =>
        string.Equals(NormalizeRelative(a), NormalizeRelative(b), StringComparison.OrdinalIgnoreCase);

    static bool PathStartsWith(string path, string prefix)
    {
        var p = NormalizeRelative(path);
        var px = NormalizeRelative(prefix);
        if (px.Length == 0) return false;
        return p.Equals(px, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(px + "/", StringComparison.OrdinalIgnoreCase);
    }

    static string ShaHex(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
