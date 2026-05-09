using System.Diagnostics;

namespace Imp.Substrate;

// `imp init [path] [--force]` — scaffolds the project-substrate layout
// (rules / aspirations / learnings / plans / tasks / concepts) into the
// current git repo. Templates live next to the imp DLL at runtime
// (Substrate/Templates/), copied via the csproj <None Update> entry.
//
// Substitutions at copy time:
//   {{REPO}}      → repo basename (e.g. "nb")
//   {{INIT_DATE}} → today's UTC date (YYYY-MM-DD)
//
// Refuses if non-substrate content already exists at the target. Treats
// existing _meta/conventions.md as a re-init (no-op without --force,
// regenerate skill-owned files with --force).
public static class ProjectInit
{
    const string ConventionsRelPath = "_meta/conventions.md";
    const string ClaudeMdHeading = "## Project substrate (managed by `imp init`)";

    public static int Run(string[] args)
    {
        // Parse args
        string location = "project";
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
                Console.Error.WriteLine($"  imp init project-new/");
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

        // Copy templates
        var written = CopyTreeWithSubstitution(templatesRoot, substrateDir, substitutions, overwrite: force);

        // CLAUDE.md upsert
        var claudeMdPath = Path.Combine(repoRoot, "CLAUDE.md");
        var claudeAction = UpsertClaudeMdSection(claudeMdPath, location, repoName);

        // .gitignore upsert
        var gitignorePath = Path.Combine(repoRoot, ".gitignore");
        var gitignoreLine = $"{repoName}.project-proposals/";
        var gitignoreAction = UpsertGitignoreLine(gitignorePath, gitignoreLine);

        // Summary
        Console.WriteLine($"imp init: substrate scaffolded at {RelativeTo(repoRoot, substrateDir)}/");
        Console.WriteLine($"  Files written: {written}");
        Console.WriteLine($"  CLAUDE.md: {claudeAction}");
        Console.WriteLine($"  .gitignore: {gitignoreAction}");
        Console.WriteLine();
        Console.WriteLine("Layout: rules/ aspirations/ learnings/ plans/{active,archive}/ tasks/ concepts/ reference/ + log.md, _meta/conventions.md");
        Console.WriteLine();
        Console.WriteLine("Next:");
        Console.WriteLine($"  - Read {RelativeTo(repoRoot, substrateDir)}/_meta/conventions.md for the spec.");
        Console.WriteLine($"  - When new work starts, drop a plan in {RelativeTo(repoRoot, substrateDir)}/plans/active/<slug>.md (state: exploring).");
        Console.WriteLine($"  - imp proposals will land at ../{repoName}.project-proposals/ (sidecar of repo root, gitignored).");

        return 0;
    }

    static void PrintUsage()
    {
        Console.WriteLine("usage: imp init [path] [--force]");
        Console.WriteLine();
        Console.WriteLine("  path     substrate location relative to repo root (default: project)");
        Console.WriteLine("  --force  regenerate skill-owned files (READMEs, conventions.md) on re-init");
        Console.WriteLine();
        Console.WriteLine("Refuses if path exists with non-substrate content.");
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

        return written;
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

            This repo uses a structured project-knowledge substrate at `{location}/`.
            Read from it before answering questions about design, intent, or
            current behavior. You may edit substrate files directly when
            capturing decisions, learnings, or plans. imp may not — it produces
            proposals you (or the human) review and approve.

            ### What's where

            - **`{location}/concepts/<topic>.md`** — auto-generated synthesis
              pages. Start here for a topic overview. Don't hand-edit;
              regenerated by `/project-sync`.
            - **`{location}/rules/`** — locked-in constraints. Code violating
              these is a bug. Internal contradictions are alarming.
            - **`{location}/aspirations/`** — what we're going for. Contradictions
              are fine; code falls short by definition.
            - **`{location}/learnings/`** — discovered knowledge. Decays in
              relevance, not in truth.
            - **`{location}/plans/active/`** — primary working area. Most new work
              starts here as a plan in `state: exploring`. A plan may have an
              optional companion directory `<slug>/` for prototypes/scratch.
            - **`{location}/plans/archive/`** — concluded plans (shipped, shelved,
              abandoned).
            - **`{location}/tasks/`** — task tracking.
            - **`{location}/log.md`** — append-only chronological history.

            For drift semantics per kind, see `{location}/_meta/conventions.md`.

            ### imp proposals

            imp produces proposals at `{repoName}.project-proposals/P-NNN-<slug>.md`
            when scheduled sweeps detect promotion candidates, drift, doc rot,
            etc. Auto-approval gradient when reviewing on the user's behalf:

            - **Always-safe** (auto-apply): `log.md` appends, archive moves.
            - **Claude-approvable**: new learning entries, concept regeneration,
              candidate flags.
            - **Human-required**: rules edits, deletions, supersede markers,
              anything that loses information.
            """;
    }

    static string UpsertGitignoreLine(string gitignorePath, string line)
    {
        if (File.Exists(gitignorePath))
        {
            var existing = File.ReadAllText(gitignorePath);
            // exact-line match (avoid mis-matching a substring)
            var lines = existing.Split('\n').Select(l => l.TrimEnd('\r'));
            if (lines.Any(l => l == line))
            {
                return "line already present (no-op)";
            }
            var separator = existing.EndsWith('\n') ? "" : "\n";
            File.WriteAllText(gitignorePath, existing + separator + line + "\n");
            return "appended proposals path";
        }
        else
        {
            File.WriteAllText(gitignorePath, line + "\n");
            return "created with proposals path";
        }
    }

    static string RelativeTo(string baseDir, string fullPath)
    {
        var rel = Path.GetRelativePath(baseDir, fullPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
