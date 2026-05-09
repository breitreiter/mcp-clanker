using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Imp;

// Sidecar archive for `imp wiki`, mirroring ResearchArchive's pattern:
//   <parent>/<repo>.wikis/W-NNN-<slug>/
//     manifest.json   — orchestrator state: plan + per-target progress
//     meta.json       — small index { started_at, completed_at, ... }
//     transcript.md   — concatenated per-target transcripts (later)
//
// The manifest is the load-bearing artefact for resume. Written before any
// dispatch and re-written after each per-target update; a kill mid-run
// leaves a recoverable state.

public enum WikiEntryStatus
{
    Pending,    // not yet attempted (or attempted and crashed mid-flight)
    Done,       // completed successfully (run, skip, or stub recorded)
    Skipped,    // SHA matched existing page; no work performed
    Failed,     // dispatch returned a terminal-error envelope
}

public sealed record WikiManifestEntry(
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("page_path")] string PagePath,
    [property: JsonPropertyName("decision")] WikiDecision Decision,
    [property: JsonPropertyName("source_tree_sha")] string SourceTreeSha,
    [property: JsonPropertyName("source_bytes")] long SourceBytes,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("status")] WikiEntryStatus Status,
    [property: JsonPropertyName("research_id")] string? ResearchId,
    [property: JsonPropertyName("research_archive")] string? ResearchArchive,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("error")] string? Error);

public sealed record WikiManifest(
    [property: JsonPropertyName("wiki_id")] string WikiId,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("repo_root")] string RepoRoot,
    [property: JsonPropertyName("wiki_dir")] string WikiDir,
    [property: JsonPropertyName("max_dir_bytes")] long MaxDirBytes,
    [property: JsonPropertyName("tool_budget")] int ToolBudget,
    [property: JsonPropertyName("target_subpath")] string TargetSubpath,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("targets")] List<WikiManifestEntry> Targets);

public static class WikiArchive
{
    public static string RootFor(string repoRoot)
    {
        var absRoot = Path.GetFullPath(repoRoot);
        var name = Path.GetFileName(absRoot.TrimEnd(Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(absRoot)
            ?? throw new InvalidOperationException($"Repo root '{absRoot}' has no parent directory.");
        return Path.Combine(parent, $"{name}.wikis");
    }

    public static string DirectoryFor(string repoRoot, string wikiId, string slug)
        => Path.Combine(RootFor(repoRoot), $"{wikiId}-{slug}");

    public static string ManifestPath(string archiveDir)
        => Path.Combine(archiveDir, "manifest.json");

    public static string AllocateNextId(string repoRoot)
    {
        var root = RootFor(repoRoot);
        if (!Directory.Exists(root)) return "W-001";

        int max = 0;
        var rx = new Regex(@"^W-(\d+)(?:-|$)", RegexOptions.Compiled);
        foreach (var entry in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(entry);
            var m = rx.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
                max = Math.Max(max, n);
        }
        return $"W-{(max + 1):D3}";
    }

    // Find an archive directory by wiki ID prefix. Returns null if no match.
    // Used by --resume to locate W-NNN-<unknown-slug>/.
    public static string? FindByWikiId(string repoRoot, string wikiId)
    {
        var root = RootFor(repoRoot);
        if (!Directory.Exists(root)) return null;
        var match = Directory.EnumerateDirectories(root)
            .FirstOrDefault(d =>
            {
                var name = Path.GetFileName(d);
                return name.Equals(wikiId, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(wikiId + "-", StringComparison.OrdinalIgnoreCase);
            });
        return match;
    }

    public static void WriteManifest(string archiveDir, WikiManifest manifest)
    {
        Directory.CreateDirectory(archiveDir);
        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        File.WriteAllText(ManifestPath(archiveDir), json);
    }

    public static WikiManifest? ReadManifest(string archiveDir)
    {
        var path = ManifestPath(archiveDir);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<WikiManifest>(json, JsonOpts);
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
        },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
