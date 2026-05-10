using System.Diagnostics;

namespace Imp.Substrate;

// `imp init [path] [--force]` — scaffolds the substrate layout into
// the current git repo. Default location is `imp/` (gnome-maintained
// substrate). Also scaffolds root-level human-owned dirs: `plans/`,
// `bugs/`, `rules/`, and `TODO.md` if missing.
//
// Templates live next to the imp DLL at runtime (Substrate/Templates/),
// copied via the csproj <None Update> entry.
//
// Substitutions at copy time:
//   {{REPO}}      → repo basename (e.g. "nb")
//   {{INIT_DATE}} → today's UTC date (YYYY-MM-DD)
//
// Refuses if non-substrate content already exists at the target. Treats
// existing _meta/conventions.md as a re-init (no-op without --force,
// regenerate skill-owned files with --force). Root-level dirs and
// TODO.md are never overwritten — created only if missing.
public static class ProjectInit
{
    const string ConventionsRelPath = "_meta/conventions.md";
    const string ClaudeMdHeading = "## Project substrate (managed by `imp init`)";

    public static int Run(string[] args)
    {
        // Parse args
        string location = "imp";
        bool force = false;
        foreach (var arg in args)
        {
            if (arg is "--force" or "-f") force = true;
            else if (arg is "--help" or "-h") { PrintUsage(); return 0; }
            else if (arg.StartsWith('-')) { Console.Error.WriteLine($"imp init: unknown flag '{arg}'"); return 1; }
            else location = arg.TrimEnd('/', '\\');
        }

        if (Path.IsPathRooted(location))
        {
            Console.Error.WriteLine("imp init: location must be a relative path inside the repo");
            return 1;
        }

        // Verify git context and resolve repo root
        var repoRoot = GitRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("imp init: not in a git repo. Run `git init` first.");
            return 1;
        }
        var repoName = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar));

        // Resolve substrate path
        var substrateDir = Path.GetFullPath(Path.Combine(repoRoot, location));
        if (!substrateDir.StartsWith(repoRoot, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("imp init: location escapes repo root");
            return 1;
        }

        // Existing-content check
        var conventionsFile = Path.Combine(substrateDir, ConventionsRelPath);
        if (Directory.Exists(substrateDir))
        {
            if (File.Exists(conventionsFile))
            {
                if (!force)
                {
                    Console.WriteLine($"imp init: substrate already exists at {RelativeTo(repoRoot, substrateDir)}/");
                    Console.WriteLine("Use --force to regenerate skill-owned files (READMEs, conventions.md).");
                    return 0;
                }
                // fall through: re-init
            }
            else if (HasNonHiddenContent(substrateDir))
            {
                Console.Error.WriteLine($"imp init: {RelativeTo(repoRoot, substrateDir)}/ exists with non-substrate content.");
                Console.Error.WriteLine("Run /project-migrate (planned) to ingest legacy docs, or pick a different location:");
                Console.Error.WriteLine($"  imp init imp-new/");
                return 1;
            }
        }

        // Templates source
        var exeDir = AppContext.BaseDirectory;
        var templatesRoot = Path.Combine(exeDir, "Substrate", "Templates");
        if (!Directory.Exists(templatesRoot))
        {
            Console.Error.WriteLine($"imp init: templates not found at {templatesRoot}");
            Console.Error.WriteLine("(this is an installation issue — the imp build is missing Substrate/Templates content)");
            return 2;
        }

        // Substitutions
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var substitutions = new Dictionary<string, string>
        {
            ["{{REPO}}"] = repoName,
            ["{{INIT_DATE}}"] = today,
        };

        // Copy templates (substrate dir)
        var written = CopyTreeWithSubstitution(templatesRoot, substrateDir, substitutions, overwrite: force);

        // Scaffold root-level human-owned dirs (only if missing)
        var rootScaffold = ScaffoldRootDirs(repoRoot);

        // CLAUDE.md upsert
        var claudeMdPath = Path.Combine(repoRoot, "CLAUDE.md");
        var claudeAction = UpsertClaudeMdSection(claudeMdPath, location, repoName);

        // .gitignore upsert (proposals + .imp/ cache)
        var gitignorePath = Path.Combine(repoRoot, ".gitignore");
        var gitignoreLines = new[]
        {
            $"{repoName}.imp-proposals/",
            ".imp/",
        };
        var gitignoreAction = UpsertGitignoreLines(gitignorePath, gitignoreLines);

        // Summary
        Console.WriteLine($"imp init: substrate scaffolded at {RelativeTo(repoRoot, substrateDir)}/");
        Console.WriteLine($"  Files written: {written}");
        if (rootScaffold.Count > 0)
        {
            Console.WriteLine($"  Root scaffolded: {string.Join(", ", rootScaffold)}");
        }
        Console.WriteLine($"  CLAUDE.md: {claudeAction}");
        Console.WriteLine($"  .gitignore: {gitignoreAction}");
        Console.WriteLine();
        Console.WriteLine($"Layout (under {RelativeTo(repoRoot, substrateDir)}/, gnome-maintained):");
        Console.WriteLine("  learnings/ reference/ concepts/ _index/ note/ log.md _meta/");
        Console.WriteLine("Layout (repo root, human-owned):");
        Console.WriteLine("  plans/ bugs/ rules/ TODO.md");
        Console.WriteLine();
        Console.WriteLine("Next:");
        Console.WriteLine($"  - Read {RelativeTo(repoRoot, substrateDir)}/_meta/conventions.md for the shape.");
        Console.WriteLine($"  - Drop a capture: imp note \"<text>\" — the gnome processes it on `imp tidy`.");
        Console.WriteLine($"  - imp proposals (cross-boundary changes only) land at ../{repoName}.imp-proposals/.");

        // Legacy-content hint: if `project/` exists and isn't this substrate,
        // surface the migrate path. The init-seed skill should seed parent
        // knowledge first; /project-migrate folds existing prose docs in.
        var legacyProject = Path.Combine(repoRoot, "project");
        if (Directory.Exists(legacyProject)
            && !PathEqualsIgnoreCase(legacyProject, substrateDir)
            && !File.Exists(Path.Combine(legacyProject, "_meta", "conventions.md")))
        {
            Console.WriteLine();
            Console.WriteLine("Legacy content detected at project/. After the parent agent seeds");
            Console.WriteLine($"the substrate with what it knows from context, run `imp migrate` then");
            Console.WriteLine("/project-migrate to ingest the existing docs into substrate entries.");
        }

        return 0;
    }

    static bool PathEqualsIgnoreCase(string a, string b)
    {
        var na = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar);
        var nb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    static void PrintUsage()
    {
        Console.WriteLine("usage: imp init [path] [--force]");
        Console.WriteLine();
        Console.WriteLine("  path     substrate location relative to repo root (default: imp)");
        Console.WriteLine("  --force  regenerate skill-owned files (READMEs, conventions.md) on re-init");
        Console.WriteLine();
        Console.WriteLine("Refuses if path exists with non-substrate content.");
        Console.WriteLine("Also scaffolds root-level human-owned dirs (plans/, bugs/, rules/) and TODO.md");
        Console.WriteLine("if missing — never overwrites existing root content.");
    }

    static string? GitRepoRoot()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.StartInfo.ArgumentList.Add("rev-parse");
            proc.StartInfo.ArgumentList.Add("--show-toplevel");
            proc.Start();
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(true); } catch { }
                return null;
            }
            return proc.ExitCode == 0 && stdout.Length > 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }

    static bool HasNonHiddenContent(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.')) continue;  // .git, .keep, etc.
            return true;
        }
        return false;
    }

    static int CopyTreeWithSubstitution(string srcDir, string dstDir, Dictionary<string, string> substitutions, bool overwrite)
    {
        Directory.CreateDirectory(dstDir);
        int written = 0;

        foreach (var srcPath in Directory.EnumerateFileSystemEntries(srcDir))
        {
            var name = Path.GetFileName(srcPath);
            var dstPath = Path.Combine(dstDir, name);

            if (Directory.Exists(srcPath))
            {
                written += CopyTreeWithSubstitution(srcPath, dstPath, substitutions, overwrite);
            }
            else
            {
                if (File.Exists(dstPath) && !overwrite) continue;
                var content = File.ReadAllText(srcPath);
                foreach (var (key, value) in substitutions)
                {
                    content = content.Replace(key, value, StringComparison.Ordinal);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                File.WriteAllText(dstPath, content);
                written++;
            }
        }

        // If this directory ended up empty (e.g. note/inbox/, _index/by-file/),
        // drop a .gitkeep so git tracks it. Idempotent — won't add a duplicate.
        if (!Directory.EnumerateFileSystemEntries(dstDir).Any())
        {
            File.WriteAllText(Path.Combine(dstDir, ".gitkeep"), "");
            written++;
        }

        return written;
    }

    // Creates root-level human-owned dirs and TODO.md if missing. Never
    // overwrites existing content. Returns list of created entries for
    // the summary.
    static List<string> ScaffoldRootDirs(string repoRoot)
    {
        var created = new List<string>();

        foreach (var dirName in new[] { "plans", "bugs", "rules" })
        {
            var path = Path.Combine(repoRoot, dirName);
            if (Directory.Exists(path)) continue;
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
            created.Add($"{dirName}/");
        }

        var todoPath = Path.Combine(repoRoot, "TODO.md");
        if (!File.Exists(todoPath))
        {
            File.WriteAllText(todoPath, "# TODO\n\n<!-- running list of work to do; substrate-aware -->\n");
            created.Add("TODO.md");
        }

        return created;
    }

    static string UpsertClaudeMdSection(string claudeMdPath, string location, string repoName)
    {
        var section = BuildClaudeMdSection(location, repoName);

        if (File.Exists(claudeMdPath))
        {
            var existing = File.ReadAllText(claudeMdPath);
            if (existing.Contains(ClaudeMdHeading, StringComparison.Ordinal))
            {
                return "section already present (no-op)";
            }
            var separator = existing.EndsWith('\n') ? "\n" : "\n\n";
            File.WriteAllText(claudeMdPath, existing + separator + section);
            return "appended substrate section";
        }
        else
        {
            File.WriteAllText(claudeMdPath, section);
            return "created with substrate section";
        }
    }

    static string BuildClaudeMdSection(string location, string repoName)
    {
        return $"""
            {ClaudeMdHeading}

            This repo uses a structured project-knowledge substrate split between
            `{location}/` (gnome-maintained) and root-level human-owned dirs.
            Read substrate content before answering questions about design,
            intent, or current behavior.

            ### What's where

            **`{location}/` — gnome territory** (imp writes directly under
            `imp-gnome <noreply@imp.local>`):

            - **`{location}/concepts/<topic>.md`** — auto-generated narrative
              synthesis pages. Don't hand-edit; regenerated by `imp tidy`.
            - **`{location}/_index/`** — per-file/symbol/feature lookup pages.
              Read `{location}/_index/by-file/<path>.md` before editing a
              source file for a digest of what to know first.
            - **`{location}/learnings/`** — discovered knowledge, why-decisions,
              gotchas. Authored by the gnome from notes.
            - **`{location}/reference/`** — archived external sources (URLs +
              local snippets). Authored by the gnome from notes.
            - **`{location}/note/inbox/`** — write target for `imp note`. The
              gnome processes captures here into structured entries on
              `imp tidy`.
            - **`{location}/log.md`** — append-only history.

            **Repo root — human territory:**

            - **`plans/`** — design intent, specs, in-flight work. Most new
              work starts as a plan in `state: exploring`.
            - **`bugs/`** — bug reports.
            - **`TODO.md`** — running list.
            - **`rules/`** — hard project invariants. Substrate-shaped
              (frontmatter, drift tracking) but human-authored.

            For drift semantics per kind, see `{location}/_meta/conventions.md`.

            ### imp proposals

            Imp writes its own dir directly. For changes touching root-level
            human dirs (`rules/`, `plans/`, `bugs/`, `TODO.md`), imp produces
            proposals at `{repoName}.imp-proposals/P-NNN-<slug>.md`. Review
            and apply via `/imp-promote`. Auto-approval gradient when Claude
            reviews on the user's behalf:

            - **Always-safe** (auto-apply): `TODO.md` appends.
            - **Claude-approvable**: plan edits and state-flips, new
              exploring plans.
            - **Human-required**: any change to `rules/`, deletions, anything
              that loses information.
            """;
    }

    static string UpsertGitignoreLines(string gitignorePath, IReadOnlyList<string> linesToAdd)
    {
        var preexisting = File.Exists(gitignorePath);
        var existing = preexisting ? File.ReadAllText(gitignorePath) : "";
        var existingLines = existing.Split('\n').Select(l => l.TrimEnd('\r')).ToHashSet();

        var added = new List<string>();
        foreach (var line in linesToAdd)
        {
            if (!existingLines.Contains(line))
            {
                added.Add(line);
                existingLines.Add(line);
            }
        }

        if (added.Count == 0) return "lines already present (no-op)";

        var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
        var appended = string.Join("\n", added) + "\n";
        File.WriteAllText(gitignorePath, existing + separator + appended);
        return preexisting
            ? $"appended: {string.Join(", ", added)}"
            : $"created with: {string.Join(", ", added)}";
    }

    static string RelativeTo(string baseDir, string fullPath)
    {
        var rel = Path.GetRelativePath(baseDir, fullPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
