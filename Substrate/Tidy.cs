using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Imp.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI.Embeddings;

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

    public static async Task<int> RunAsync(IChatClient chat, IConfiguration config, string[] args)
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

        var triagePromptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "tidy-triage.md");
        var draftPromptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "tidy-draft.md");
        var draftPatchPromptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "tidy-draft-patch.md");
        var proposalPromptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "tidy-proposal.md");
        var locatePromptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "tidy-locate.md");
        if (!File.Exists(triagePromptPath) || !File.Exists(draftPromptPath) || !File.Exists(draftPatchPromptPath)
            || !File.Exists(proposalPromptPath) || !File.Exists(locatePromptPath))
        {
            Console.Error.WriteLine($"imp tidy: prompt files not found at {Path.GetDirectoryName(triagePromptPath)}");
            return 2;
        }
        var triageSystemPrompt = await File.ReadAllTextAsync(triagePromptPath);
        var draftSystemPrompt = await File.ReadAllTextAsync(draftPromptPath);
        var patchSystemPrompt = await File.ReadAllTextAsync(draftPatchPromptPath);
        var proposalSystemPrompt = await File.ReadAllTextAsync(proposalPromptPath);
        var locateSystemPrompt = await File.ReadAllTextAsync(locatePromptPath);

        // Refresh the embeddings cache before processing notes. Always run
        // — even on empty inbox — so substrate-only edits between tidy
        // runs (people fixing typos, marking plans shipped) keep the
        // cache aligned. Fail-closed if EmbeddingProvider is unreachable
        // per rules/embedding-provider.md.
        var embed = Embeddings.Create(config);
        var modelId = config["EmbeddingProvider:Model"] ?? "unknown";
        Console.WriteLine("imp tidy: refreshing embeddings cache…");
        var refreshSw = Stopwatch.StartNew();
        var refreshStats = await EmbeddingIndex.RefreshAsync(embed, repoRoot, modelId);
        refreshSw.Stop();
        Console.WriteLine(
            $"  embeddings: added={refreshStats.Added} updated={refreshStats.Updated} " +
            $"removed={refreshStats.Removed} unchanged={refreshStats.Unchanged} " +
            $"({refreshSw.ElapsedMilliseconds}ms)");

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

        var proposalsDir = ResolveProposalsDir(repoRoot);

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
                            chat, embed,
                            draftSystemPrompt, patchSystemPrompt, locateSystemPrompt,
                            repoRoot, substrateDir, notePath, noteName,
                            body, frontmatter, triage, dryRun, stats);
                        break;

                    case "rule-suggestion":
                    case "plan-suggestion":
                        await HandleProposalAsync(
                            chat, proposalSystemPrompt,
                            substrateDir, proposalsDir, notePath, noteName,
                            body, frontmatter, triage, dryRun, stats);
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
        [property: JsonPropertyName("reference_fields")] ReferenceFields? ReferenceFields,
        [property: JsonPropertyName("discard_reason")] string? DiscardReason);

    sealed record TriageTouches(
        [property: JsonPropertyName("files")] List<string> Files,
        [property: JsonPropertyName("symbols")] List<string> Symbols,
        [property: JsonPropertyName("features")] List<string> Features);

    sealed record ReferenceFields(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("subject")] string Subject);

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

    static async Task<string?> DraftBodyAsync(IChatClient chat, string systemPrompt, string body, IReadOnlyDictionary<string, string> noteMeta, TriageResult triage)
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

    // ── phase 4b: patch draft (update existing entry) ────────────

    // Patches an existing entry in place: reads it, calls the patch
    // prompt with its body + the note + triage, writes the result back
    // with frontmatter preserved (only `updated:` bumped). Returns true
    // if the update succeeded, false if anything went wrong and the
    // caller should fall back to create.
    static async Task<bool> ApplyPatchAsync(
        IChatClient chat, string patchPrompt,
        string repoRoot, string targetRelPath,
        string notePath, string noteName,
        string noteBody, IReadOnlyDictionary<string, string> noteFrontmatter,
        TriageResult triage, bool dryRun, RunStats stats)
    {
        var absPath = Path.Combine(repoRoot, targetRelPath);
        string existingContent;
        try { existingContent = await File.ReadAllTextAsync(absPath); }
        catch (IOException ex)
        {
            ImpLog.Warn($"tidy.patch: read failed for {targetRelPath}: {ex.Message}");
            Console.WriteLine($"      patch: read failed; falling back to create");
            return false;
        }

        var (fmBlock, existingBody) = SplitFrontmatter(existingContent);
        if (fmBlock is null)
        {
            // No frontmatter — refuse to patch. The orchestrator owns
            // frontmatter; a body-only file shouldn't be in the substrate.
            Console.WriteLine($"      patch: {targetRelPath} has no frontmatter; falling back to create");
            return false;
        }

        var patchedBody = await PatchBodyAsync(
            chat, patchPrompt, existingBody, noteBody, noteFrontmatter, triage);
        if (string.IsNullOrWhiteSpace(patchedBody))
        {
            Console.WriteLine($"      patch: model returned empty body; falling back to create");
            return false;
        }
        // Defensive: strip any frontmatter the model emitted despite the
        // prompt instruction not to.
        patchedBody = StripLeadingFrontmatter(patchedBody).TrimStart('\r', '\n');

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var bumpedFm = BumpUpdatedField(fmBlock, today);
        var newContent = $"---\n{bumpedFm}\n---\n\n{patchedBody}";
        if (!newContent.EndsWith('\n')) newContent += "\n";

        Console.WriteLine($"      patch → {targetRelPath}");
        if (dryRun) { stats.Updated++; return true; }

        await File.WriteAllTextAsync(absPath, newContent);
        MoveTo(notePath, Path.Combine(repoRoot, ResolveSubstrateRel(repoRoot), "note", "processed"));
        stats.Updated++;
        return true;
    }

    static async Task<string?> PatchBodyAsync(
        IChatClient chat, string systemPrompt,
        string existingBody, string noteBody,
        IReadOnlyDictionary<string, string> noteFrontmatter,
        TriageResult triage)
    {
        var user = BuildPatchUserMessage(existingBody, noteBody, noteFrontmatter, triage);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, user),
        };
        var resp = await chat.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = 2000 });
        var text = resp.Text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    static string BuildPatchUserMessage(
        string existingBody, string noteBody,
        IReadOnlyDictionary<string, string> noteFrontmatter,
        TriageResult triage)
    {
        var sb = new StringBuilder();
        sb.Append("## Existing entry body\n\n");
        sb.Append(existingBody.Trim());
        sb.Append("\n\n## Inbox note (the new content to incorporate)\n\n");
        sb.Append($"triage classification: {triage.Classification}\n");
        sb.Append($"triage title: {triage.Title}\n");
        sb.Append($"triage rationale: {triage.Rationale}\n\n");
        sb.Append("note body:\n");
        sb.Append(noteBody.Trim());
        sb.Append("\n\nProduce the revised body markdown. Body only — no frontmatter, no surrounding prose.");
        return sb.ToString();
    }

    // Splits a file into its raw frontmatter block (text between the
    // opening and closing `---`) and the body after it. Returns
    // (null, content) if no frontmatter is detected.
    //
    // Unlike ParseNote, this preserves the frontmatter as raw text so
    // we can edit it surgically (e.g. bump `updated:`) without
    // re-serializing through a flat key/value model that would lose
    // nested YAML structure.
    static (string? FrontmatterBlock, string Body) SplitFrontmatter(string content)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal)
            && !content.StartsWith("---\r\n", StringComparison.Ordinal))
            return (null, content);

        var afterOpen = content.IndexOf('\n') + 1;
        var endIdx = content.IndexOf("\n---", afterOpen, StringComparison.Ordinal);
        if (endIdx < 0) return (null, content);

        var fmBlock = content[afterOpen..endIdx];
        var bodyStart = endIdx + "\n---".Length;
        if (bodyStart < content.Length && content[bodyStart] == '\r') bodyStart++;
        if (bodyStart < content.Length && content[bodyStart] == '\n') bodyStart++;
        if (bodyStart < content.Length && content[bodyStart] == '\r') bodyStart++;
        if (bodyStart < content.Length && content[bodyStart] == '\n') bodyStart++;
        return (fmBlock, content[bodyStart..]);
    }

    // Sets the `updated:` field in a raw frontmatter block to `today`.
    // Operates on lines; matches a top-level (column-0) `updated:` field
    // and ignores any nested `updated:` keys at deeper indentation.
    // Inserts the field after `created:` if missing entirely.
    static string BumpUpdatedField(string fmBlock, string today)
    {
        var lines = fmBlock.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var rstripped = line.TrimEnd('\r');
            if (rstripped.StartsWith("updated:", StringComparison.Ordinal))
            {
                lines[i] = $"updated: {today}";
                return string.Join("\n", lines);
            }
        }
        // Field missing — insert after `created:` if present, else at the end.
        var output = new List<string>(lines.Length + 1);
        bool inserted = false;
        foreach (var line in lines)
        {
            output.Add(line);
            if (!inserted && line.TrimEnd('\r').StartsWith("created:", StringComparison.Ordinal))
            {
                output.Add($"updated: {today}");
                inserted = true;
            }
        }
        if (!inserted) output.Add($"updated: {today}");
        return string.Join("\n", output);
    }

    // Returns "imp" or "project" depending on which substrate is in use.
    // Tidy already calls FindSubstrateDir during RunAsync; this helper
    // re-derives the relative name for places we need to compute paths
    // off the substrate root without threading the dir param everywhere.
    static string ResolveSubstrateRel(string repoRoot)
    {
        var imp = Path.Combine(repoRoot, "imp", "_meta", "conventions.md");
        if (File.Exists(imp)) return "imp";
        return "project";
    }

    // ── phase 6: apply ────────────────────────────────────────────

    static async Task HandleEntryAsync(
        IChatClient chat, EmbeddingClient embed,
        string draftPrompt, string patchPrompt, string locatePrompt,
        string repoRoot, string substrateDir, string notePath, string noteName,
        string body, IReadOnlyDictionary<string, string> frontmatter,
        TriageResult triage, bool dryRun, RunStats stats)
    {
        // Phase 2: locate. Decide create-new vs update-which.
        Locate.Result? located = null;
        try
        {
            located = await Locate.LocateAsync(
                chat, embed, repoRoot,
                triage.Classification, triage.Title, body, locatePrompt);
            Console.WriteLine($"      locate: {located.Decision}" +
                (located.TargetPath is not null ? $" → {located.TargetPath}" : "") +
                $"  ({located.Candidates.Count} cand)");
            if (located.Candidates.Count > 0)
            {
                var top = located.Candidates[0];
                Console.WriteLine($"        top candidate: {top.Entry.Path} (sim={top.Similarity:F4})");
            }
        }
        catch (Exception ex)
        {
            // Locate failure → fall through to create. Better to make
            // progress with a duplicate-able entry than to leave the note
            // unprocessed.
            ImpLog.Warn($"tidy.locate: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"      locate: failed ({ex.GetType().Name}); proceeding with create");
        }

        // If locate decided update, route to patch.
        if (located is not null
            && string.Equals(located.Decision, "update", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(located.TargetPath))
        {
            var updated = await ApplyPatchAsync(
                chat, patchPrompt,
                repoRoot, located.TargetPath,
                notePath, noteName,
                body, frontmatter, triage, dryRun, stats);
            if (updated) return;
            // ApplyPatchAsync logged the reason; fall through to create.
        }

        var bodyMarkdown = await DraftBodyAsync(chat, draftPrompt, body, frontmatter, triage);
        if (string.IsNullOrWhiteSpace(bodyMarkdown))
        {
            Console.WriteLine($"      draft failed → leaving in inbox");
            stats.Failed++;
            return;
        }
        // Defensive: if the model emitted a frontmatter block despite the
        // prompt's instruction not to, strip it. The orchestrator owns
        // frontmatter; double frontmatter is invalid.
        bodyMarkdown = StripLeadingFrontmatter(bodyMarkdown).TrimStart('\r', '\n');

        var subdir = triage.Classification == "learning" ? "learnings" : "reference";
        var slug = SlugifyTitle(triage.Title);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var entryDir = Path.Combine(substrateDir, subdir);
        var (filename, fullPath) = ResolveEntryPath(entryDir, today, slug);

        var sourceNoteId = Path.GetFileNameWithoutExtension(noteName);
        var fm = BuildFrontmatter(triage, today, sourceNoteId);
        var entry = $"---\n{fm}---\n\n{bodyMarkdown}";
        if (!entry.EndsWith('\n')) entry += "\n";

        Console.WriteLine($"      → {Path.Combine(subdir, filename).Replace('\\', '/')}");
        if (dryRun) { stats.Drafted++; return; }

        Directory.CreateDirectory(entryDir);
        await File.WriteAllTextAsync(fullPath, entry);
        MoveTo(notePath, Path.Combine(substrateDir, "note", "processed"));

        if (triage.Classification == "learning") stats.Learnings++;
        else stats.References++;
    }

    // Builds the YAML frontmatter block for an entry. Code owns this — the
    // model owns body only. Canonical line format (one field per line).
    static string BuildFrontmatter(TriageResult triage, string todayUtc, string sourceNoteId)
    {
        var sb = new StringBuilder();
        sb.Append($"kind: {triage.Classification}\n");
        sb.Append($"title: {triage.Title}\n");
        sb.Append($"created: {todayUtc}\n");
        sb.Append($"updated: {todayUtc}\n");
        sb.Append($"status: current\n");

        sb.Append("touches:\n");
        sb.Append($"  files: [{FormatYamlList(triage.Touches?.Files)}]\n");
        sb.Append($"  symbols: [{FormatYamlList(triage.Touches?.Symbols)}]\n");
        sb.Append($"  features: [{FormatYamlList(triage.Touches?.Features)}]\n");

        sb.Append("provenance:\n");
        sb.Append($"  author: imp-gnome\n");
        sb.Append($"  origin: note:{sourceNoteId}\n");

        if (triage.Classification == "reference" && triage.ReferenceFields is { } rf)
        {
            // Bare strings without surrounding quotes work for typical URLs and
            // short subject lines. If we hit YAML-special chars in the future
            // (`:`, `#`, leading `-`), revisit and quote.
            sb.Append($"url: {rf.Url}\n");
            sb.Append($"subject: {rf.Subject}\n");
        }

        return sb.ToString();
    }

    static string FormatYamlList(IReadOnlyList<string>? items)
    {
        if (items is null || items.Count == 0) return "";
        return string.Join(", ", items);
    }

    // Removes a leading YAML frontmatter block from text, defensively, if the
    // model emitted one despite the prompt's instruction.
    static string StripLeadingFrontmatter(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal) && !text.StartsWith("---\r\n", StringComparison.Ordinal))
            return text;
        var rest = text.AsSpan(text.IndexOf('\n') + 1);
        var endIdx = rest.IndexOf("\n---", StringComparison.Ordinal);
        if (endIdx < 0) return text;
        return rest[(endIdx + 4)..].ToString();
    }

    // ── proposal handling (cross-boundary: rule-suggestion / plan-suggestion) ──

    sealed record ProposalPhaseResult(
        [property: JsonPropertyName("rationale")] string Rationale,
        [property: JsonPropertyName("preview_body")] string PreviewBody);

    static async Task HandleProposalAsync(
        IChatClient chat, string proposalPrompt,
        string substrateDir, string proposalsDir,
        string notePath, string noteName,
        string body, IReadOnlyDictionary<string, string> frontmatter,
        TriageResult triage, bool dryRun, RunStats stats)
    {
        var phaseOutput = await DraftProposalAsync(chat, proposalPrompt, body, frontmatter, triage);
        if (phaseOutput is null || string.IsNullOrWhiteSpace(phaseOutput.Rationale) || string.IsNullOrWhiteSpace(phaseOutput.PreviewBody))
        {
            Console.WriteLine($"      proposal phase failed → leaving in inbox");
            stats.Failed++;
            return;
        }

        var targetKind = triage.Classification == "rule-suggestion" ? "rule" : "plan";
        var targetDir = targetKind == "rule" ? "rules" : "plans";
        var slug = SlugifyTitle(triage.Title);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var sourceNoteId = Path.GetFileNameWithoutExtension(noteName);

        Directory.CreateDirectory(proposalsDir);
        var proposalId = NextProposalId(proposalsDir, today);
        var proposalFilename = $"{proposalId}-{slug}.md";
        var proposalFullPath = Path.Combine(proposalsDir, proposalFilename);

        var targetEntryPath = $"{targetDir}/{slug}.md";
        var proposalContent = BuildProposal(
            proposalId, today, triage,
            phaseOutput.Rationale, phaseOutput.PreviewBody,
            targetKind, targetEntryPath, sourceNoteId);

        Console.WriteLine($"      → {proposalsDir.TrimEnd('/').Split('/').Last()}/{proposalFilename}");

        if (dryRun) { stats.Proposed++; return; }

        await File.WriteAllTextAsync(proposalFullPath, proposalContent);
        MoveTo(notePath, Path.Combine(substrateDir, "note", "processed"));
        stats.Proposed++;
    }

    static async Task<ProposalPhaseResult?> DraftProposalAsync(IChatClient chat, string systemPrompt, string body, IReadOnlyDictionary<string, string> noteMeta, TriageResult triage)
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

        var json = ExtractJsonObject(text);
        if (json is null) { ImpLog.Warn($"tidy.proposal: response not parseable JSON: {text[..Math.Min(200, text.Length)]}"); return null; }

        try
        {
            return JsonSerializer.Deserialize<ProposalPhaseResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException ex)
        {
            ImpLog.Warn($"tidy.proposal: deserialize failed: {ex.Message}");
            return null;
        }
    }

    static string BuildProposal(
        string proposalId, string todayUtc, TriageResult triage,
        string rationale, string previewBody,
        string targetKind, string targetEntryPath, string sourceNoteId)
    {
        var nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var category = triage.Classification == "rule-suggestion" ? "rule_candidate" : "plan_candidate";

        // Preview frontmatter — the would-be entry's frontmatter, code-built.
        var pfm = new StringBuilder();
        pfm.Append($"kind: {targetKind}\n");
        pfm.Append($"title: {triage.Title}\n");
        pfm.Append($"created: {todayUtc}\n");
        pfm.Append($"updated: {todayUtc}\n");
        pfm.Append($"status: current\n");
        if (targetKind == "plan") pfm.Append($"state: exploring\n");
        pfm.Append("touches:\n");
        pfm.Append($"  files: [{FormatYamlList(triage.Touches?.Files)}]\n");
        pfm.Append($"  symbols: [{FormatYamlList(triage.Touches?.Symbols)}]\n");
        pfm.Append($"  features: [{FormatYamlList(triage.Touches?.Features)}]\n");
        pfm.Append("provenance:\n");
        pfm.Append($"  author: imp-gnome\n");
        pfm.Append($"  origin: note:{sourceNoteId}\n");

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append($"proposal_id: {proposalId}\n");
        sb.Append($"generated_at: {nowIso}\n");
        sb.Append($"generated_by: imp-tidy\n");
        sb.Append($"category: {category}\n");
        sb.Append($"status: pending\n");
        sb.Append("---\n\n");
        sb.Append($"# Proposal: {triage.Title}\n\n");
        sb.Append("## Rationale\n\n");
        sb.Append(rationale.Trim()).Append("\n\n");
        sb.Append("## Proposed changes\n\n");
        sb.Append("```yaml\n");
        sb.Append("changes:\n");
        sb.Append($"  - type: create\n");
        sb.Append($"    path: {targetEntryPath}\n");
        sb.Append($"    preview: preview-1\n");
        sb.Append("```\n\n");
        sb.Append("## Preview: preview-1\n\n");
        sb.Append("````markdown\n");
        sb.Append("---\n");
        sb.Append(pfm);
        sb.Append("---\n\n");
        sb.Append(previewBody.Trim()).Append("\n");
        sb.Append("````\n");
        return sb.ToString();
    }

    // Default proposals dir: parent of repo root, sibling of repo, named
    // "<repo-basename>.imp-proposals". Matches `imp init`'s gitignore upsert.
    static string ResolveProposalsDir(string repoRoot)
    {
        var repoBase = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(repoRoot.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new InvalidOperationException($"repo root has no parent: {repoRoot}");
        return Path.Combine(parent, $"{repoBase}.imp-proposals");
    }

    // Allocates the next P-YYYY-MM-DD-NNN id in the proposals dir for today's
    // date. Scans existing P-{date}-*.md filenames, finds max NNN, increments.
    static string NextProposalId(string proposalsDir, string date)
    {
        int next = 1;
        if (Directory.Exists(proposalsDir))
        {
            foreach (var path in Directory.EnumerateFiles(proposalsDir, $"P-{date}-*.md", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                // Format: P-YYYY-MM-DD-NNN-<slug> — split on '-' and take index 4
                var parts = name.Split('-', 6);
                if (parts.Length < 5) continue;
                if (int.TryParse(parts[4], out var seq) && seq >= next) next = seq + 1;
            }
        }
        return $"P-{date}-{next:D3}";
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
                    $"{stats.Proposed} proposal(s), {stats.Discarded} discarded, " +
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
        public int Updated;          // existing entries patched via locate→update
        public int Proposed;
        public int Drafted;          // dry-run only
        public int Discarded;
        public int Failed;

        public int Total => Learnings + References + Updated + Proposed + Drafted + Discarded + Failed;
        public bool AnyChanges => Learnings > 0 || References > 0 || Updated > 0 || Proposed > 0 || Discarded > 0;

        public string Summary() =>
            $"{Total} note(s): {Learnings + References + Drafted} new entry(s), {Updated} updated, {Proposed} proposal(s), {Discarded} discarded, {Failed} failed";
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
