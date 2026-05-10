using System.Diagnostics;
using System.Text;

namespace Imp.Substrate;

// `imp note <text>` — append a capture to the substrate's note inbox.
// The 90% case is a parent agent calling `imp note "<one paragraph>"`
// during conversation; the gnome (`imp tidy`, future) processes inbox
// items overnight and proposes structured layer-1 entries.
//
// Input modes:
//   imp note <text>     positional arg
//   imp note            opens $EDITOR (vi fallback)
//   imp note -          reads stdin
//
// Resolves substrate root by looking under the git repo root for either
// imp/_meta/conventions.md (new layout) or project/_meta/conventions.md
// (legacy). Note files land at <substrate>/note/inbox/.
//
// Auto-captures: UTC timestamp, repo name, IMP_SOURCE env (default "cli"),
// short git HEAD. Filename is <YYYY-MM-DD-HHMMSS>-<slug>.md.
public static class Note
{
    public static int Run(string[] args)
    {
        // Parse args
        bool readStdin = false;
        string? text = null;
        foreach (var a in args)
        {
            if (a is "--help" or "-h") { PrintUsage(); return 0; }
            if (a == "-") { readStdin = true; continue; }
            if (a.StartsWith('-')) { Console.Error.WriteLine($"imp note: unknown flag '{a}'"); return 1; }
            if (text is not null) { Console.Error.WriteLine("imp note: too many positional arguments (quote multi-word text)"); return 1; }
            text = a;
        }

        // Resolve substrate root
        var cwd = Directory.GetCurrentDirectory();
        var repoRoot = GitRepoRoot(cwd);
        if (repoRoot is null)
        {
            Console.Error.WriteLine("imp note: not in a git repo");
            return 1;
        }

        var substrateDir = FindSubstrateDir(repoRoot);
        if (substrateDir is null)
        {
            Console.Error.WriteLine("imp note: no substrate found (expected imp/_meta/conventions.md or project/_meta/conventions.md). Run `imp init` first.");
            return 1;
        }

        // Acquire body
        string body;
        if (readStdin)
        {
            body = Console.In.ReadToEnd();
        }
        else if (text is not null)
        {
            body = text;
        }
        else
        {
            var (ok, edited) = OpenEditor();
            if (!ok) return 1;
            body = edited;
        }

        body = body.Trim();
        if (body.Length == 0)
        {
            Console.Error.WriteLine("imp note: empty input, nothing captured");
            return 1;
        }

        // Compose
        var now = DateTime.UtcNow;
        var timestamp = now.ToString("yyyy-MM-dd-HHmmss");
        var slug = Slugify(body);
        var repoName = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar));
        var source = Environment.GetEnvironmentVariable("IMP_SOURCE")?.Trim() ?? "";
        if (source.Length == 0) source = "cli";
        var (gitOk, gitOut) = RunGit(repoRoot, "rev-parse", "--short=12", "HEAD");
        var gitHead = gitOk ? gitOut.Trim() : "";

        var inbox = Path.Combine(substrateDir, "note", "inbox");
        Directory.CreateDirectory(inbox);

        var (filename, fullPath) = ResolvePath(inbox, timestamp, slug);

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append($"captured: {now:yyyy-MM-ddTHH:mm:ssZ}\n");
        sb.Append($"repo: {repoName}\n");
        sb.Append($"source: {source}\n");
        if (gitHead.Length > 0) sb.Append($"git-head: {gitHead}\n");
        sb.Append("---\n\n");
        sb.Append(body);
        if (!body.EndsWith('\n')) sb.Append('\n');

        File.WriteAllText(fullPath, sb.ToString());

        // Confirmation tuned for Claude relay
        var echo = body.Length > 80 ? body[..77].ReplaceLineEndings(" ") + "..." : body.ReplaceLineEndings(" ");
        Console.WriteLine($"noted {Path.GetFileNameWithoutExtension(filename)}: {echo}");
        return 0;
    }

    // ── input helpers ─────────────────────────────────────────────

    static (bool Ok, string Text) OpenEditor()
    {
        var editor = Environment.GetEnvironmentVariable("EDITOR");
        if (string.IsNullOrWhiteSpace(editor)) editor = "vi";

        var tmp = Path.Combine(Path.GetTempPath(), $"imp-note-{Guid.NewGuid():N}.md");
        try
        {
            File.WriteAllText(tmp, "");
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", $"{editor} {EscapeShell(tmp)}" },
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Console.Error.WriteLine($"imp note: failed to launch editor '{editor}'");
                return (false, "");
            }
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine($"imp note: editor exited non-zero ({proc.ExitCode}); aborting");
                return (false, "");
            }
            return (true, File.ReadAllText(tmp));
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    static string EscapeShell(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // ── path helpers ──────────────────────────────────────────────

    static string? FindSubstrateDir(string repoRoot)
    {
        foreach (var name in new[] { "imp", "project" })
        {
            var candidate = Path.Combine(repoRoot, name);
            if (File.Exists(Path.Combine(candidate, "_meta", "conventions.md"))) return candidate;
        }
        return null;
    }

    static (string Filename, string FullPath) ResolvePath(string inbox, string timestamp, string slug)
    {
        var baseName = string.IsNullOrEmpty(slug) ? timestamp : $"{timestamp}-{slug}";
        var path = Path.Combine(inbox, baseName + ".md");
        if (!File.Exists(path)) return (baseName + ".md", path);
        for (int i = 2; i < 100; i++)
        {
            var candidate = $"{baseName}-{i}";
            var candidatePath = Path.Combine(inbox, candidate + ".md");
            if (!File.Exists(candidatePath)) return (candidate + ".md", candidatePath);
        }
        // 100 collisions in one second — give up cleanly
        var fallback = $"{baseName}-{Guid.NewGuid():N}";
        return (fallback + ".md", Path.Combine(inbox, fallback + ".md"));
    }

    static string Slugify(string body)
    {
        // First non-empty line, first ~6 words, [a-z0-9-] only, max 40 chars
        var firstLine = "";
        foreach (var line in body.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) { firstLine = t; break; }
        }

        var words = firstLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var take = words.Length < 6 ? words.Length : 6;

        var sb = new StringBuilder();
        for (int i = 0; i < take; i++)
        {
            if (sb.Length > 0) sb.Append('-');
            foreach (var ch in words[i].ToLowerInvariant())
            {
                if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
                else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            }
        }
        var slug = sb.ToString().Trim('-');
        // Collapse runs of dashes from punctuation (e.g. "rename:" → "rename-")
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug;
    }

    // ── git ───────────────────────────────────────────────────────

    static string? GitRepoRoot(string startDir)
    {
        var (ok, stdout) = RunGit(startDir, "rev-parse", "--show-toplevel");
        return ok ? stdout.Trim() : null;
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
            if (!proc.WaitForExit(5_000))
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

    // ── usage ─────────────────────────────────────────────────────

    static void PrintUsage()
    {
        Console.WriteLine("""
Usage: imp note [<text> | -]

Append a capture to the substrate's note inbox. The gnome processes
inbox items into structured layer-1 entries on a later `imp tidy` run.

Modes:
  imp note "<text>"   capture positional arg (the dominant case)
  imp note            open $EDITOR (vi fallback) on a temp file
  imp note -          read stdin

Auto-captures timestamp, repo name, IMP_SOURCE env, and short git HEAD
into frontmatter. Files land at <substrate>/note/inbox/.

Substrate is detected under the git repo root at one of:
  imp/_meta/conventions.md       (new layout)
  project/_meta/conventions.md   (legacy)

Run `imp init` first if neither exists.
""");
    }
}
