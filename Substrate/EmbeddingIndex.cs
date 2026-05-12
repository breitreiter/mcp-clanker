using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Imp.Infrastructure;
using OpenAI.Embeddings;

namespace Imp.Substrate;

// Per-substrate embedding cache. Doc-level: one vector per layer-1 entry
// or root-level human-owned doc. Backing store is `.imp/embeddings.jsonl`
// (gitignored — see plans/embeddings-and-tidy-phase2.md, "Future
// direction"). One JSON record per line; rewritten atomically and sorted
// by path on every save.
//
// Embedding input is `{title}\n\n{body}` (title from frontmatter,
// falling back to filename; body is everything after the closing `---`).
// Hash is hex-SHA256 of that exact input string — if the input changes
// at all (incl. whitespace), the entry is stale and re-embedded.
//
// Cache integrity invariants enforced on load + refresh:
//   - dim must match ExpectedDim (4096 for Qwen3-Embedding-8B)
//   - model name is recorded but not validated; soft signal for the
//     rules/embedding-provider.md story
public static class EmbeddingIndex
{
    public const string CacheRelPath = ".imp/embeddings.jsonl";
    public const int ExpectedDim = 4096;

    static readonly string[] ScanDirs = ["imp/learnings", "imp/reference", "plans", "rules"];

    public sealed record Entry(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("content_hash")] string ContentHash,
        [property: JsonPropertyName("dim")] int Dim,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("vector")] float[] Vector);

    public sealed record RefreshStats(int Added, int Updated, int Removed, int Unchanged)
    {
        public int Total => Added + Updated + Removed + Unchanged;
    }

    // ── enumeration ───────────────────────────────────────────────

    // Top-level `*.md` in the four scan dirs, excluding README.md.
    // Returns posix-style paths relative to repoRoot, sorted.
    public static List<string> EnumerateEntries(string repoRoot)
    {
        var result = new List<string>();
        foreach (var d in ScanDirs)
        {
            var abs = System.IO.Path.Combine(repoRoot, d);
            if (!Directory.Exists(abs)) continue;
            foreach (var f in Directory.EnumerateFiles(abs, "*.md", SearchOption.TopDirectoryOnly))
            {
                var name = System.IO.Path.GetFileName(f);
                if (string.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = System.IO.Path.GetRelativePath(repoRoot, f).Replace('\\', '/');
                result.Add(rel);
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    // ── load / save ───────────────────────────────────────────────

    public static Dictionary<string, Entry> Load(string repoRoot)
    {
        var path = System.IO.Path.Combine(repoRoot, CacheRelPath);
        var dict = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!File.Exists(path)) return dict;

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        int line = 0;
        foreach (var raw in File.ReadLines(path))
        {
            line++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            Entry? entry;
            try { entry = JsonSerializer.Deserialize<Entry>(raw, opts); }
            catch (JsonException ex)
            {
                ImpLog.Warn($"EmbeddingIndex.Load: parse error at {path}:{line}: {ex.Message}");
                continue;
            }
            if (entry is null || entry.Vector.Length != entry.Dim || entry.Dim != ExpectedDim)
            {
                ImpLog.Warn($"EmbeddingIndex.Load: skipping dim-mismatched entry {entry?.Path} (dim={entry?.Dim}, vec={entry?.Vector.Length})");
                continue;
            }
            dict[entry.Path] = entry;
        }
        return dict;
    }

    public static void Save(string repoRoot, IEnumerable<Entry> entries)
    {
        var path = System.IO.Path.Combine(repoRoot, CacheRelPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";

        var opts = new JsonSerializerOptions { WriteIndented = false };
        using (var sw = new StreamWriter(tmp))
        {
            foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
                sw.WriteLine(JsonSerializer.Serialize(entry, opts));
        }
        File.Move(tmp, path, overwrite: true);
    }

    // ── refresh ───────────────────────────────────────────────────

    public static async Task<RefreshStats> RefreshAsync(EmbeddingClient client, string repoRoot, string modelId)
    {
        var existing = Load(repoRoot);
        var paths = EnumerateEntries(repoRoot);

        var toEmbed = new List<(string Path, string Input, string Hash)>();
        var keep = new Dictionary<string, Entry>(StringComparer.Ordinal);
        int unchanged = 0;

        foreach (var rel in paths)
        {
            var abs = System.IO.Path.Combine(repoRoot, rel);
            string content;
            try { content = await File.ReadAllTextAsync(abs); }
            catch (IOException ex)
            {
                ImpLog.Warn($"EmbeddingIndex.Refresh: read failed for {rel}: {ex.Message}");
                continue;
            }

            var (input, hash) = ComputeInputAndHash(content, rel);

            if (existing.TryGetValue(rel, out var prev)
                && prev.ContentHash == hash
                && prev.Dim == ExpectedDim)
            {
                keep[rel] = prev;
                unchanged++;
            }
            else
            {
                toEmbed.Add((rel, input, hash));
            }
        }

        int added = 0, updated = 0;
        if (toEmbed.Count > 0)
        {
            var vectors = await Embeddings.EmbedBatchAsync(
                client,
                toEmbed.Select(t => t.Input).ToList());
            for (int i = 0; i < toEmbed.Count; i++)
            {
                var t = toEmbed[i];
                var v = vectors[i];
                if (v.Length != ExpectedDim)
                    throw new InvalidOperationException(
                        $"EmbeddingIndex.Refresh: server returned dim={v.Length}, expected {ExpectedDim} for {t.Path}");
                keep[t.Path] = new Entry(t.Path, t.Hash, v.Length, modelId, v);
                if (existing.ContainsKey(t.Path)) updated++;
                else added++;
            }
        }

        int removed = existing.Keys.Count(k => !keep.ContainsKey(k));
        Save(repoRoot, keep.Values);
        return new RefreshStats(added, updated, removed, unchanged);
    }

    // ── content addressing ───────────────────────────────────────

    // Returns (embedding-input-string, hex-sha256(embedding-input)).
    // The same input string is what gets sent to the embedding server,
    // so hash collisions imply embed-result equivalence (modulo model
    // determinism). `relPath` is the fallback title when frontmatter
    // doesn't carry one.
    public static (string Input, string Hash) ComputeInputAndHash(string fileContent, string relPath)
    {
        var (fm, body) = ParseFrontmatter(fileContent);
        var title = fm.TryGetValue("title", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : System.IO.Path.GetFileNameWithoutExtension(relPath);

        var input = $"{title}\n\n{body.Trim()}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return (input, hash);
    }

    // Minimal YAML frontmatter parser — same shape as Tidy.ParseNote.
    // Single-line key:value pairs only; we don't need nested fields here
    // because `title:` is the only thing we read.
    static (Dictionary<string, string> Frontmatter, string Body) ParseFrontmatter(string content)
    {
        var fm = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!content.StartsWith("---\n", StringComparison.Ordinal)
            && !content.StartsWith("---\r\n", StringComparison.Ordinal))
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
            if (!fm.ContainsKey(key)) fm[key] = value;
        }

        var body = rest[(endIdx + 4)..].ToString().TrimStart('\r', '\n');
        return (fm, body);
    }
}
