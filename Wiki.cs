using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Imp;

// Orchestrator for `imp wiki`. Step 4 of project/wiki-plan.md.
//
// Consumes a WikiManifest (constructed by the CLI from a fresh plan or
// loaded from disk for --resume), iterates targets sequentially, and:
//   - Run    — synthesises a per-target brief, dispatches Research.RunAsync
//              against the wiki mode, captures research_id and outcome.
//   - Skip   — marks the entry skipped (page already up-to-date).
//   - Stub   — marks the entry done (the renderer in step 5 will write the
//              actual stub markdown; the orchestrator just records intent).
//
// Manifest is rewritten after every per-target update so a kill leaves a
// recoverable state. On resume, entries with status in {Done, Skipped} are
// passed over; Pending and Failed are retried (Failed implies the prior
// dispatch returned a terminal-error envelope; a fresh dispatch is fine).
//
// Markdown page rendering is NOT done here — that's step 5. After this
// step runs, the user has report.json files in <repo>.researches/ and a
// manifest in <repo>.wikis/, but no wiki/*.md yet.

public sealed record WikiResult(
    [property: JsonPropertyName("wiki_id")] string WikiId,
    [property: JsonPropertyName("repo_root")] string RepoRoot,
    [property: JsonPropertyName("wiki_dir")] string WikiDir,
    [property: JsonPropertyName("archive_dir")] string ArchiveDir,
    [property: JsonPropertyName("pages_run")] int PagesRun,
    [property: JsonPropertyName("pages_skipped")] int PagesSkipped,
    [property: JsonPropertyName("pages_stub")] int PagesStub,
    [property: JsonPropertyName("pages_failed")] int PagesFailed,
    [property: JsonPropertyName("wall_seconds")] double WallSeconds);

public static class Wiki
{
    public const int DefaultToolBudget = 10;

    // Build a manifest from a fresh plan. The manifest is the canonical
    // working state from here on; the plan is its construction input only.
    public static WikiManifest ManifestFromPlan(
        WikiPlan plan,
        string targetSubpath,
        int toolBudget,
        string slug)
    {
        var entries = plan.Targets.Select(t => new WikiManifestEntry(
            SourcePath: t.RelativePath,
            PagePath: t.PagePath,
            Decision: t.Decision,
            SourceTreeSha: t.SourceTreeSha,
            SourceBytes: t.SourceBytes,
            FileCount: t.FileCount,
            Status: WikiEntryStatus.Pending,
            ResearchId: null,
            ResearchArchive: null,
            StartedAt: null,
            CompletedAt: null,
            Error: null)).ToList();

        return new WikiManifest(
            WikiId: WikiArchive.AllocateNextId(plan.RepoRoot),
            Slug: slug,
            RepoRoot: plan.RepoRoot,
            WikiDir: plan.WikiDir,
            MaxDirBytes: plan.MaxDirBytes,
            ToolBudget: toolBudget,
            TargetSubpath: targetSubpath,
            CreatedAt: DateTimeOffset.UtcNow,
            Targets: entries);
    }

    public static async Task<string> RunAsync(
        IChatClient chat,
        IConfiguration config,
        WikiManifest manifest,
        string archiveDir)
    {
        ImpLog.Info($"wiki: start wikiId={manifest.WikiId} archive={archiveDir} targets={manifest.Targets.Count}");
        var sw = Stopwatch.StartNew();

        Directory.CreateDirectory(archiveDir);
        WikiArchive.WriteManifest(archiveDir, manifest);

        for (int i = 0; i < manifest.Targets.Count; i++)
        {
            var entry = manifest.Targets[i];
            if (entry.Status == WikiEntryStatus.Done || entry.Status == WikiEntryStatus.Skipped)
            {
                ImpLog.Info($"wiki: target {i + 1}/{manifest.Targets.Count} {entry.SourcePath} status={entry.Status} (resume skip)");
                continue;
            }

            ImpLog.Info($"wiki: target {i + 1}/{manifest.Targets.Count} {entry.SourcePath} decision={entry.Decision}");
            var updated = await ProcessTargetAsync(chat, config, manifest, entry);
            manifest.Targets[i] = updated;
            WikiArchive.WriteManifest(archiveDir, manifest);
        }

        sw.Stop();

        var result = new WikiResult(
            WikiId: manifest.WikiId,
            RepoRoot: manifest.RepoRoot,
            WikiDir: manifest.WikiDir,
            ArchiveDir: archiveDir,
            PagesRun: manifest.Targets.Count(t => t.Decision == WikiDecision.Run && t.Status == WikiEntryStatus.Done),
            PagesSkipped: manifest.Targets.Count(t => t.Status == WikiEntryStatus.Skipped),
            PagesStub: manifest.Targets.Count(t => t.Decision == WikiDecision.Stub && t.Status == WikiEntryStatus.Done),
            PagesFailed: manifest.Targets.Count(t => t.Status == WikiEntryStatus.Failed),
            WallSeconds: Math.Round(sw.Elapsed.TotalSeconds, 2));

        ImpLog.Info($"wiki: complete wikiId={manifest.WikiId} run={result.PagesRun} skipped={result.PagesSkipped} stub={result.PagesStub} failed={result.PagesFailed} wall={result.WallSeconds}s");

        return JsonSerializer.Serialize(result, ResultJsonOpts);
    }

    static async Task<WikiManifestEntry> ProcessTargetAsync(
        IChatClient chat,
        IConfiguration config,
        WikiManifest manifest,
        WikiManifestEntry entry)
    {
        var startedAt = DateTimeOffset.UtcNow;

        switch (entry.Decision)
        {
            case WikiDecision.Skip:
                return entry with
                {
                    Status = WikiEntryStatus.Skipped,
                    StartedAt = startedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                };

            case WikiDecision.Stub:
                // Step 5 (renderer) will write the stub page on disk; step 4
                // just records the decision in the manifest. The renderer
                // reads manifest entries with decision=Stub and emits the
                // canonical oversized-stub markdown.
                return entry with
                {
                    Status = WikiEntryStatus.Done,
                    StartedAt = startedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                };

            case WikiDecision.Run:
                return await DispatchRunAsync(chat, config, manifest, entry, startedAt);

            default:
                return entry with
                {
                    Status = WikiEntryStatus.Failed,
                    StartedAt = startedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = $"unknown decision: {entry.Decision}",
                };
        }
    }

    static async Task<WikiManifestEntry> DispatchRunAsync(
        IChatClient chat,
        IConfiguration config,
        WikiManifest manifest,
        WikiManifestEntry entry,
        DateTimeOffset startedAt)
    {
        var descriptor = BuildDescriptor(manifest, entry);

        string envelope;
        try
        {
            envelope = await Research.RunAsync(
                chat: chat,
                config: config,
                modeName: "wiki",
                descriptor: descriptor,
                repoRoot: manifest.RepoRoot,
                toolBudgetOverride: manifest.ToolBudget);
        }
        catch (Exception ex)
        {
            ImpLog.Error($"wiki: dispatch failed sourcePath={entry.SourcePath} researchId={descriptor.ResearchId}: {ex.Message}");
            return entry with
            {
                Status = WikiEntryStatus.Failed,
                ResearchId = descriptor.ResearchId,
                ResearchArchive = ResearchArchive.DirectoryFor(manifest.RepoRoot, descriptor),
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = ex.Message,
            };
        }

        var (terminal, error) = ParseEnvelopeOutcome(envelope);
        var researchArchive = ResearchArchive.DirectoryFor(manifest.RepoRoot, descriptor);
        var status = terminal == "success" ? WikiEntryStatus.Done : WikiEntryStatus.Failed;

        return entry with
        {
            Status = status,
            ResearchId = descriptor.ResearchId,
            ResearchArchive = researchArchive,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Error = status == WikiEntryStatus.Failed ? (error ?? terminal ?? "unknown") : null,
        };
    }

    // Synthesise a TaskDescriptor for a wiki survey of the target directory.
    // The brief mirrors the YAML shape from project/wiki-plan.md but is
    // emitted as the markdown brief format the rest of the codebase already
    // round-trips through (## R-NNN: title, **Question:**, etc.) so the
    // brief.md sidecar stays human-readable and consistent with research-mode
    // archives.
    static TaskDescriptor BuildDescriptor(WikiManifest manifest, WikiManifestEntry entry)
    {
        var researchId = BriefParser.AllocateNextId(manifest.RepoRoot);
        var displayPath = entry.SourcePath.Length == 0 ? "<repo root>" : entry.SourcePath;
        var slug = BriefParser.SlugFrom("wiki-" + (entry.SourcePath.Length == 0 ? "root" : entry.SourcePath));

        var sources = WikiPlanner.EnumerateSourceFiles(manifest.RepoRoot, entry.SourcePath);

        var question = $"Produce a directory survey of {displayPath} suitable for a wiki page that describes the contents of this directory to a reader who has not seen the source.";

        var subQuestions = new[]
        {
            "What does each file in this directory do?",
            "What are the entrypoints into this directory's code from elsewhere in the repo?",
            "What are the load-bearing types or functions?",
        };

        var background = string.Join("\n",
            $"You are running in wiki mode against {displayPath}.",
            $"Stay inside this directory; only read outside to resolve a cross-reference.",
            $"Tool budget for this run is {manifest.ToolBudget} calls.");

        var expectedOutput = string.Join("\n",
            "- synthesis (<=80 words)",
            "- findings[] (one per meaningful file in the target, plus cross-cutting findings)",
            "- coverage with explored / not_explored / gaps; account for every file in the target");

        var sourceMarkdown = BuildBriefMarkdown(researchId, slug, question, background, subQuestions, sources, expectedOutput);

        return new TaskDescriptor(
            ResearchId: researchId,
            Slug: slug,
            Question: question,
            SubQuestions: subQuestions,
            SuggestedSources: sources,
            Forbidden: Array.Empty<string>(),
            Background: background,
            ExpectedOutput: expectedOutput,
            SourceMarkdown: sourceMarkdown);
    }

    static string BuildBriefMarkdown(
        string researchId,
        string slug,
        string question,
        string background,
        IReadOnlyList<string> subQuestions,
        IReadOnlyList<string> sources,
        string expectedOutput)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(researchId).Append(": ").Append(slug).Append('\n').Append('\n');
        sb.Append("**Question:** ").Append(question).Append('\n').Append('\n');
        sb.Append("**Background:**\n").Append(background).Append('\n').Append('\n');
        sb.Append("**Sub-questions:**\n");
        foreach (var q in subQuestions) sb.Append("- ").Append(q).Append('\n');
        sb.Append('\n');
        if (sources.Count > 0)
        {
            sb.Append("**Sources:**\n");
            foreach (var s in sources) sb.Append("- ").Append(s).Append('\n');
            sb.Append('\n');
        }
        sb.Append("**Output:**\n").Append(expectedOutput).Append('\n');
        return sb.ToString();
    }

    // Pull terminal_state out of the JSON envelope returned by Research.RunAsync.
    // Single field probe — avoids re-deserialising the whole thing or coupling
    // to ResearchResult's record type.
    static (string? Terminal, string? Error) ParseEnvelopeOutcome(string envelope)
    {
        try
        {
            using var doc = JsonDocument.Parse(envelope);
            var terminal = doc.RootElement.TryGetProperty("terminal_state", out var ts) ? ts.GetString() : null;
            string? error = null;
            if (doc.RootElement.TryGetProperty("blocked_reason", out var br) && br.ValueKind == JsonValueKind.Object)
            {
                if (br.TryGetProperty("summary", out var ex)) error = ex.GetString();
            }
            return (terminal, error);
        }
        catch
        {
            return (null, "could not parse research envelope");
        }
    }

    static readonly JsonSerializerOptions ResultJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
