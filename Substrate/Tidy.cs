using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Imp.Infrastructure;
using Microsoft.Extensions.AI;

namespace Imp.Substrate;

// `imp tidy` — the gnome. Processes notes from `imp/note/inbox/` into
// structured layer-1 entries.
//
// v0a scope (this file):
//   1. Triage: classify each note (learning | reference | rule-suggestion |
//      plan-suggestion | noise). LLM call.
//   4. Draft: for learning/reference, generate the entry markdown. LLM call.
//   6. Apply: write entry into imp/learnings/ or imp/reference/, move source
//      note to imp/note/processed/ or discarded/<reason>/.
//   7. Commit: stage imp/* changes, commit under imp-gnome author.
//   Plus: append a summary entry to imp/log.md.
//
// Deferred to later versions:
//   - Phase 2 (locate existing entry to edit) — v0a always creates new files.
//   - Phase 3 (read existing context) — n/a until phase 2 lands.
//   - Phase 5 (verify) — schema check of frontmatter is the only check today.
//   - Cross-boundary proposals (rule-suggestion / plan-suggestion) — v0a moves
//     these to discarded/cross-boundary-deferred/ for v0b processing.
//   - URL fetch + Wayback archiving for references.
//   - Drift detection on existing entries.
//   - Layer 0 cache, layer 2 generation, _index/ regen.
//
// Flags:
//   --dry-run   show what would happen without writing or committing.

public static class Tidy
{
    const string ImpGnomeAuthorName = "imp-gnome";
    const string ImpGnomeAuthorEmail = "noreply@imp.local";

    public static async Task<int> RunAsync(IChatClient chat, string[] args)
    {
        bool dryRun = false;
        foreach (var a in args)
        {
            if (a is "--help" or "-h") { PrintUsage(); return 0; }
            if (a is "--dry-run" or "-n") { dryRun = true; continue; }
            Console.Error.WriteLine($"imp tidy: unknown flag '{a}'");
            return 1;
        }

        var cwd = Directory.GetCurrentDirectory();
        var repoRoot = GitRepoRoot(cwd);
        if (repoRoot is null) { Console.Error.WriteLine("imp tidy: not in a git repo"); return 1; }

        var substrateDir = FindSubstrateDir(repoRoot);
        if (substrateDir is null)
        {
            Console.Error.WriteLine("imp tidy: no substrate found (expected imp/_meta/conventions.md). Run `imp init` first.");
            return 1;
        }

        var inbox = Path.Combine(substrateDir, "note", "inbox");
        var notes = Directory.Exists(inbox)
            ? Directory.EnumerateFiles(inbox, "*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList()
            : new List<string>();

        if (notes.Count == 0)
        {
            Console.WriteLine("imp tidy: no notes pending.");
            return 0;
        }

        Console.WriteLine($"imp tidy: {notes.Count} note(s) pending{(dryRun ? " (dry run)" : "")}.");

        var triagePromptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "tidy-triage.md");
        var draftPromptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "tidy-draft.md");
        if (!File.Exists(triagePromptPath) || !File.Exists(draftPromptPath))
        {
            Console.Error.WriteLine($"imp tidy: prompt files not found at {Path.GetDirectoryName(triagePromptPath)}");
            return 2;
        }
        var triageSystemPrompt = await File.ReadAllTextAsync(triagePromptPath);
        var draftSystemPrompt = await File.ReadAllTextAsync(draftPromptPath);

        var stats = new RunStats();

        foreach (var notePath in notes)
        {
            try
            {
                var noteName = Path.GetFileName(notePath);
                Console.WriteLine($"  • {noteName}");

                var (frontmatter, body) = ParseNote(await File.ReadAllTextAsync(notePath));
                if (string.IsNullOrWhiteSpace(body))
                {
                    Console.WriteLine($"      empty body → discarding (reason: empty)");
                    if (!dryRun) MoveTo(notePath, Path.Combine(substrateDir, "note", "discarded", "empty"));
                    stats.Discarded++;
                    continue;
                }

                // Phase 1: triage
                var triage = await TriageAsync(chat, triageSystemPrompt, body, frontmatter);
                if (triage is null)
                {
                    Console.WriteLine($"      triage failed → leaving in inbox");
                    stats.Failed++;
                    continue;
                }
                Console.WriteLine($"      triage: {triage.Classification}  \"{triage.Title}\"");

                switch (triage.Classification)
                {
                    case "learning":
                    case "reference":
                        await HandleEntryAsync(
                            chat, draftSystemPrompt,
                            substrateDir, notePath, noteName,
                            body, frontmatter, triage, dryRun, stats);
                        break;

                    case "rule-suggestion":
                    case "plan-suggestion":
                        Console.WriteLine($"      cross-boundary → deferred (v0a)");
                        if (!dryRun) MoveTo(notePath, Path.Combine(substrateDir, "note", "discarded", "cross-boundary-deferred"));
                        stats.Deferred++;
                        break;

                    case "noise":
                        var reason = string.IsNullOrWhiteSpace(triage.DiscardReason) ? "noise" : SlugifyReason(triage.DiscardReason);
                        Console.WriteLine($"      noise → discarding (reason: {reason})");
                        if (!dryRun) MoveTo(notePath, Path.Combine(substrateDir, "note", "discarded", reason));
                        stats.Discarded++;
                        break;

                    default:
                        Console.WriteLine($"      unrecognized classification '{triage.Classification}' → leaving in inbox");
                        stats.Failed++;
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"      error: {ex.GetType().Name}: {ex.Message}");
                stats.Failed++;
            }
        }

        // Append log entry
        if (!dryRun && stats.Total > 0)
        {
            AppendLogEntry(substrateDir, stats);
        }

        // Commit under imp-gnome author
        if (!dryRun && stats.AnyChanges)
        {
            var commitResult = ImpCommit(repoRoot, substrateDir, $"imp tidy: {stats.Summary()}");
            Console.WriteLine($"\ncommit: {commitResult}");
        }

        Console.WriteLine($"\n{stats.Summary()}{(dryRun ? " (dry run, nothing written)" : "")}");
        return 0;
    }

    // ── note parsing ──────────────────────────────────────────────

    static (Dictionary<string, string> Frontmatter, string Body) ParseNote(string content)
    {
        var fm = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!content.StartsWith("---\n", StringComparison.Ordinal) && !content.StartsWith("---\r\n", StringComparison.Ordinal))
            return (fm, content);

        var rest = content.AsSpan(content.IndexOf('\n') + 1);
        var endIdx = rest.IndexOf("\n---", StringComparison.Ordinal);
        if (endIdx < 0) return (fm, content);

        var fmBlock = rest[..endIdx].ToString();
        foreach (var line in fmBlock.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var sep = trimmed.IndexOf(':');
            if (sep <= 0) continue;
            var key = trimmed[..sep].Trim();
            var value = trimmed[(sep + 1)..].Trim();
            fm[key] = value;
        }

        var body = rest[(endIdx + 4)..].ToString().TrimStart('\r', '\n');
        return (fm, body);
    }

    // ── phase 1: triage ───────────────────────────────────────────

    sealed record TriageResult(
        [property: JsonPropertyName("classification")] string Classification,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("rationale")] string Rationale,
        [property: JsonPropertyName("touches")] TriageTouches Touches,
        [property: JsonPropertyName("discard_reason")] string? DiscardReason);

    sealed record TriageTouches(
        [property: JsonPropertyName("files")] List<string> Files,
        [property: JsonPropertyName("symbols")] List<string> Symbols,
        [property: JsonPropertyName("features")] List<string> Features);

    static async Task<TriageResult?> TriageAsync(IChatClient chat, string systemPrompt, string body, IReadOnlyDictionary<string, string> noteMeta)
    {
        var user = BuildTriageUserMessage(body, noteMeta);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, user),
        };
        var options = new ChatOptions { MaxOutputTokens = 600 };

        var resp = await chat.GetResponseAsync(messages, options);
        var text = resp.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        var json = ExtractJsonObject(text);
        if (json is null)
        {
            ImpLog.Warn($"tidy.triage: response was not parseable JSON: {text[..Math.Min(200, text.Length)]}");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TriageResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException ex)
        {
            ImpLog.Warn($"tidy.triage: deserialize failed: {ex.Message}");
            return null;
        }
    }

    static string BuildTriageUserMessage(string body, IReadOnlyDictionary<string, string> noteMeta)
    {
        var sb = new StringBuilder();
        sb.Append("Note metadata:\n");
        foreach (var (k, v) in noteMeta) sb.Append($"  {k}: {v}\n");
        sb.Append("\nNote body:\n\n");
        sb.Append(body);
        sb.Append("\n\nClassify per the system instructions. Output JSON only.");
        return sb.ToString();
    }

    // Be lenient with LLM output: extract first {...} block if there's prose
    // around it.
    static string? ExtractJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }

    // ── phase 4: draft ────────────────────────────────────────────

    static async Task<string?> DraftEntryAsync(IChatClient chat, string systemPrompt, string body, IReadOnlyDictionary<string, string> noteMeta, TriageResult triage)
    {
        var user = BuildDraftUserMessage(body, noteMeta, triage);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, user),
        };
        var options = new ChatOptions { MaxOutputTokens = 1500 };

        var resp = await chat.GetResponseAsync(messages, options);
        var text = resp.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text;
    }

    static string BuildDraftUserMessage(string body, IReadOnlyDictionary<string, string> noteMeta, TriageResult triage)
    {
        var sb = new StringBuilder();
        sb.Append("Note metadata:\n");
        foreach (var (k, v) in noteMeta) sb.Append($"  {k}: {v}\n");
        sb.Append("\nNote body:\n\n");
        sb.Append(body);
        sb.Append("\n\n");
        sb.Append("Triage output:\n");
        sb.Append($"  classification: {triage.Classification}\n");
        sb.Append($"  title: {triage.Title}\n");
        sb.Append($"  rationale: {triage.Rationale}\n");
        sb.Append($"  touches.files: [{string.Join(", ", triage.Touches?.Files ?? new List<string>())}]\n");
        sb.Append($"  touches.symbols: [{string.Join(", ", triage.Touches?.Symbols ?? new List<string>())}]\n");
        sb.Append($"  touches.features: [{string.Join(", ", triage.Touches?.Features ?? new List<string>())}]\n");
        sb.Append("\nWrite the entry per the system instructions. Output the entry markdown only.");
        return sb.ToString();
    }

    // ── phase 6: apply ────────────────────────────────────────────

    static async Task HandleEntryAsync(
        IChatClient chat, string draftPrompt,
        string substrateDir, string notePath, string noteName,
        string body, IReadOnlyDictionary<string, string> frontmatter,
        TriageResult triage, bool dryRun, RunStats stats)
    {
        var entry = await DraftEntryAsync(chat, draftPrompt, body, frontmatter, triage);
        if (string.IsNullOrWhiteSpace(entry))
        {
            Console.WriteLine($"      draft failed → leaving in inbox");
            stats.Failed++;
            return;
        }

        var subdir = triage.Classification == "learning" ? "learnings" : "reference";
        var slug = SlugifyTitle(triage.Title);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var entryDir = Path.Combine(substrateDir, subdir);
        var (filename, fullPath) = ResolveEntryPath(entryDir, today, slug);

        Console.WriteLine($"      → {Path.Combine(subdir, filename).Replace('\\', '/')}");
        if (dryRun) { stats.Drafted++; return; }

        Directory.CreateDirectory(entryDir);
        await File.WriteAllTextAsync(fullPath, entry.EndsWith('\n') ? entry : entry + "\n");
        MoveTo(notePath, Path.Combine(substrateDir, "note", "processed"));

        if (triage.Classification == "learning") stats.Learnings++;
        else stats.References++;
    }

    static (string Filename, string FullPath) ResolveEntryPath(string entryDir, string date, string slug)
    {
        var baseName = string.IsNullOrEmpty(slug) ? date : $"{date}-{slug}";
        var path = Path.Combine(entryDir, baseName + ".md");
        if (!File.Exists(path)) return (baseName + ".md", path);
        for (int i = 2; i < 100; i++)
        {
            var candidate = $"{baseName}-{i}";
            var cp = Path.Combine(entryDir, candidate + ".md");
            if (!File.Exists(cp)) return (candidate + ".md", cp);
        }
        var fallback = $"{baseName}-{Guid.NewGuid():N}";
        return (fallback + ".md", Path.Combine(entryDir, fallback + ".md"));
    }

    static void MoveTo(string sourcePath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, Path.GetFileName(sourcePath));
        if (File.Exists(dest))
        {
            // Append a uniqueness suffix; preserve all source notes.
            var withoutExt = Path.GetFileNameWithoutExtension(dest);
            var ext = Path.GetExtension(dest);
            for (int i = 2; i < 100; i++)
            {
                var alt = Path.Combine(destDir, $"{withoutExt}-{i}{ext}");
                if (!File.Exists(alt)) { dest = alt; break; }
            }
        }
        File.Move(sourcePath, dest);
    }

    // ── log + commit ──────────────────────────────────────────────

    static void AppendLogEntry(string substrateDir, RunStats stats)
    {
        var logPath = Path.Combine(substrateDir, "log.md");
        var existing = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
        var separator = existing.Length == 0 || existing.EndsWith('\n') ? "\n" : "\n\n";
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var entry = $"## [{date}] tidy | {stats.Summary()}\n\n" +
                    $"Processed inbox: {stats.Learnings} learning(s), {stats.References} reference(s), " +
                    $"{stats.Deferred} deferred (cross-boundary), {stats.Discarded} discarded, " +
                    $"{stats.Failed} failed.\n";
        File.WriteAllText(logPath, existing + separator + entry);
    }

    static string ImpCommit(string repoRoot, string substrateDir, string message)
    {
        var subRel = Path.GetRelativePath(repoRoot, substrateDir);

        var (stOk, stOut) = RunGit(repoRoot, "status", "--porcelain", "--", subRel);
        if (!stOk || string.IsNullOrWhiteSpace(stOut)) return "no changes to commit";

        var (addOk, _) = RunGit(repoRoot, "add", "--", subRel);
        if (!addOk) return "git add failed";

        var (cmOk, cmOut) = RunGit(
            repoRoot,
            "-c", $"user.name={ImpGnomeAuthorName}",
            "-c", $"user.email={ImpGnomeAuthorEmail}",
            "commit", "-m", message);

        return cmOk ? "ok" : $"git commit failed: {cmOut.Trim()}";
    }

    // ── helpers ───────────────────────────────────────────────────

    static string SlugifyTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var sb = new StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 50 ? slug[..50].TrimEnd('-') : slug;
    }

    static string SlugifyReason(string reason) => SlugifyTitle(reason);

    static string? FindSubstrateDir(string repoRoot)
    {
        foreach (var name in new[] { "imp", "project" })
        {
            var candidate = Path.Combine(repoRoot, name);
            if (File.Exists(Path.Combine(candidate, "_meta", "conventions.md"))) return candidate;
        }
        return null;
    }

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
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(15_000))
            {
                try { proc.Kill(true); } catch { }
                return (false, "");
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            return (proc.ExitCode == 0, stdout.Length > 0 ? stdout : stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── stats ─────────────────────────────────────────────────────

    sealed class RunStats
    {
        public int Learnings;
        public int References;
        public int Drafted;          // dry-run only
        public int Deferred;
        public int Discarded;
        public int Failed;

        public int Total => Learnings + References + Drafted + Deferred + Discarded + Failed;
        public bool AnyChanges => Learnings > 0 || References > 0 || Deferred > 0 || Discarded > 0;

        public string Summary() =>
            $"{Total} note(s): {Learnings + Drafted} entry(s), {Deferred} deferred, {Discarded} discarded, {Failed} failed";
    }

    // ── usage ─────────────────────────────────────────────────────

    static void PrintUsage()
    {
        Console.WriteLine("""
Usage: imp tidy [--dry-run]

Process notes from imp/note/inbox/ into structured layer-1 entries.
Each note gets a triage classification (learning | reference |
rule-suggestion | plan-suggestion | noise) and, for the imp-territory
kinds (learning, reference), a generated entry written into
imp/learnings/ or imp/reference/. Cross-boundary kinds (rule, plan)
are deferred for v0b. Noise gets moved to imp/note/discarded/<reason>/.

After processing, an entry is appended to imp/log.md and the changes
are committed under imp-gnome <noreply@imp.local>.

Flags:
  --dry-run, -n   show what would happen, write nothing
""");
    }
}
