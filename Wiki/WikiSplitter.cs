using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Imp.Wiki;

// Item 11 of project/wiki-plan.md: adaptive splitting. When a directory
// exceeds Wiki:MaxDirBytes, instead of writing the v0 oversized stub, the
// orchestrator model proposes a clustering of the dir's files into chunks
// each under the threshold. The wiki orchestrator then dispatches one
// research run per cluster.
//
// Light-orchestrator pattern: single completion call, no tools. Caller
// catches all exceptions — any failure (parse error, validation failure,
// empty proposal, etc.) falls back to the v0 stub so we never end up worse
// than today.

public sealed record WikiClusterProposal(
    string Slug,
    string Rationale,
    IReadOnlyList<string> Files,
    long TotalBytes);

public sealed record WikiSplitProposal(
    string SourcePath,
    IReadOnlyList<WikiClusterProposal> Clusters);

public static class WikiSplitter
{
    const int MaxOutputTokens = 4000;
    const int MaxClusters = 10;

    public static async Task<WikiSplitProposal> ProposeAsync(
        IChatClient orchestrator,
        string repoRoot,
        string sourcePath,
        long maxDirBytes,
        CancellationToken ct = default)
    {
        var files = EnumerateFilesWithSizes(repoRoot, sourcePath);
        if (files.Count == 0)
            throw new InvalidOperationException($"No allowlisted files in {sourcePath}; nothing to cluster.");

        var systemPrompt = LoadSystemPrompt();
        var userPrompt = BuildUserPrompt(sourcePath, files, maxDirBytes);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt),
        };
        var options = new ChatOptions { MaxOutputTokens = MaxOutputTokens };

        var response = await orchestrator.GetResponseAsync(messages, options, ct);
        var raw = response.Text?.Trim();
        if (string.IsNullOrEmpty(raw))
            throw new InvalidOperationException("Orchestrator returned empty cluster proposal.");

        var json = StripCodeFences(raw);
        var parsed = ParseProposal(json);
        try
        {
            var validated = Validate(parsed, files, maxDirBytes);
            return new WikiSplitProposal(sourcePath, validated);
        }
        catch (Exception ex)
        {
            // Surface the raw proposal alongside the validation error so
            // diagnostics can see what the orchestrator actually proposed.
            throw new InvalidOperationException(
                $"{ex.Message}\n\nRaw proposal:\n{json}", ex);
        }
    }

    static IReadOnlyList<(string Path, long Bytes)> EnumerateFilesWithSizes(string repoRoot, string sourcePath)
    {
        // Reuse the planner's enumeration (allowlisted extensions only),
        // then look up sizes via the filesystem. Faster than a second
        // git-ls-tree because the files are guaranteed tracked.
        var paths = WikiPlanner.EnumerateSourceFiles(repoRoot, sourcePath);
        var result = new List<(string, long)>();
        foreach (var p in paths)
        {
            var abs = Path.Combine(repoRoot, p);
            long bytes = 0;
            try { bytes = new FileInfo(abs).Length; } catch { /* leave 0 */ }
            result.Add((p, bytes));
        }
        return result;
    }

    static string BuildUserPrompt(string sourcePath, IReadOnlyList<(string Path, long Bytes)> files, long maxDirBytes)
    {
        var sb = new StringBuilder();
        sb.Append("Source path: ").Append(sourcePath).Append('\n');
        sb.Append("Per-cluster byte threshold: ").Append(maxDirBytes).Append('\n');
        sb.Append("Total bytes: ").Append(files.Sum(f => f.Bytes)).Append('\n');
        sb.Append("File count: ").Append(files.Count).Append("\n\n");
        sb.Append("Files:\n\n");
        sb.Append("| path | bytes |\n");
        sb.Append("|---|---|\n");
        foreach (var (path, bytes) in files)
            sb.Append("| ").Append(path).Append(" | ").Append(bytes).Append(" |\n");
        sb.Append('\n');
        sb.Append("Propose clusters per the system instructions. Output JSON only.");
        return sb.ToString();
    }

    static string LoadSystemPrompt()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "wiki-cluster-proposal.md");
        return File.ReadAllText(path);
    }

    // Tolerate models that wrap JSON in ``` fences despite the explicit "no
    // code fences" instruction. Strips a leading ```json or ``` line and a
    // trailing ``` line if present.
    static string StripCodeFences(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```")) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;
        var body = trimmed[(firstNewline + 1)..];
        var lastFence = body.LastIndexOf("```");
        return lastFence >= 0 ? body[..lastFence].Trim() : body.Trim();
    }

    static List<RawCluster> ParseProposal(string json)
    {
        var doc = JsonSerializer.Deserialize<RawProposal>(json, JsonOpts)
            ?? throw new InvalidOperationException("Cluster proposal parse returned null.");
        if (doc.Clusters is null || doc.Clusters.Count == 0)
            throw new InvalidOperationException("Cluster proposal has no clusters.");
        return doc.Clusters;
    }

    static List<WikiClusterProposal> Validate(
        List<RawCluster> raw,
        IReadOnlyList<(string Path, long Bytes)> input,
        long maxDirBytes)
    {
        if (raw.Count > MaxClusters)
            throw new InvalidOperationException($"Cluster count {raw.Count} exceeds cap {MaxClusters}.");

        var fileToBytes = input.ToDictionary(f => f.Path, f => f.Bytes, StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validated = new List<WikiClusterProposal>();

        foreach (var c in raw)
        {
            if (string.IsNullOrWhiteSpace(c.Slug))
                throw new InvalidOperationException("Cluster missing slug.");
            if (c.Files is null || c.Files.Count == 0)
                throw new InvalidOperationException($"Cluster '{c.Slug}' has no files.");

            // Validate file membership and byte sum.
            long total = 0;
            foreach (var f in c.Files)
            {
                if (!fileToBytes.TryGetValue(f, out var bytes))
                    throw new InvalidOperationException($"Cluster '{c.Slug}' references unknown file '{f}'.");
                if (!seenFiles.Add(f))
                    throw new InvalidOperationException($"File '{f}' appears in multiple clusters.");
                total += bytes;
            }

            // Bin-pack any over-budget multi-file cluster into sub-clusters.
            // Haiku reliably proposes purpose-coherent groupings but doesn't
            // always enforce the byte budget; we accept its grouping as the
            // signal and split deterministically for budget compliance.
            // Single-file clusters that exceed the threshold are unavoidable
            // (intra-file splitting is a future item) — let them through.
            if (total <= maxDirBytes || c.Files.Count == 1)
            {
                if (!seenSlugs.Add(c.Slug))
                    throw new InvalidOperationException($"Duplicate cluster slug: {c.Slug}");
                validated.Add(new WikiClusterProposal(
                    Slug: c.Slug.Trim(),
                    Rationale: c.Rationale?.Trim() ?? "",
                    Files: c.Files,
                    TotalBytes: total));
            }
            else
            {
                var subClusters = BinPack(c, fileToBytes, maxDirBytes);
                foreach (var sub in subClusters)
                {
                    if (!seenSlugs.Add(sub.Slug))
                        throw new InvalidOperationException($"Duplicate sub-cluster slug after bin-pack: {sub.Slug}");
                    validated.Add(sub);
                }
            }
        }

        // Every input file must appear somewhere.
        foreach (var (path, _) in input)
        {
            if (!seenFiles.Contains(path))
                throw new InvalidOperationException($"Input file '{path}' missing from all clusters.");
        }

        if (validated.Count > MaxClusters)
            throw new InvalidOperationException(
                $"Total cluster count {validated.Count} after bin-packing exceeds cap {MaxClusters}.");

        return validated;
    }

    // Greedy first-fit decreasing: sort files by size descending, place
    // each into the first sub-cluster that still fits (or open a new one).
    // Sub-clusters inherit the parent slug with -1, -2, ... suffixes.
    static List<WikiClusterProposal> BinPack(
        RawCluster cluster,
        Dictionary<string, long> fileToBytes,
        long maxDirBytes)
    {
        var ordered = cluster.Files
            .Select(f => (Path: f, Bytes: fileToBytes[f]))
            .OrderByDescending(t => t.Bytes)
            .ToList();

        var bins = new List<List<(string Path, long Bytes)>>();
        var binTotals = new List<long>();

        foreach (var item in ordered)
        {
            int placed = -1;
            for (int i = 0; i < bins.Count; i++)
            {
                if (binTotals[i] + item.Bytes <= maxDirBytes)
                {
                    bins[i].Add(item);
                    binTotals[i] += item.Bytes;
                    placed = i;
                    break;
                }
            }
            if (placed < 0)
            {
                bins.Add(new List<(string, long)> { item });
                binTotals.Add(item.Bytes);
            }
        }

        var result = new List<WikiClusterProposal>();
        for (int i = 0; i < bins.Count; i++)
        {
            // Maintain stable file ordering (input order) within each bin
            // so successive runs hash to the same SHA when the proposal is
            // identical.
            var orderedFiles = bins[i].Select(b => b.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
            result.Add(new WikiClusterProposal(
                Slug: bins.Count == 1 ? cluster.Slug : $"{cluster.Slug}-{i + 1}",
                Rationale: cluster.Rationale?.Trim() ?? "",
                Files: orderedFiles,
                TotalBytes: binTotals[i]));
        }
        return result;
    }

    sealed record RawProposal(
        [property: JsonPropertyName("clusters")] List<RawCluster> Clusters);

    sealed record RawCluster(
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("rationale")] string? Rationale,
        [property: JsonPropertyName("files")] List<string> Files);

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
