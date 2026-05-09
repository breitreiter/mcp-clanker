using System.Text.RegularExpressions;

namespace Imp.Wiki;

// Tiny YAML-ish frontmatter reader for wiki pages. Used by:
//   - WikiPlanner.ReadPageFrontmatter (cache-key check on source_tree_sha)
//   - WikiIndexRenderer (walks wiki/ and assembles the index from these)
//
// Pages are written by WikiPageRenderer with a known field set, so a real
// YAML parser is overkill — line-oriented regex covers the cases we emit.

public sealed record WikiPageFrontmatter(
    string? SourcePath,
    string? SourceTreeSha,
    string? Status,
    string? SynthesisSummary,
    string? GeneratedAt,
    int? SourceFilesCount,
    long? SourceBytes,
    string? ResearchId,
    string? Mode,
    string? Model,
    bool? WorktreeDirty,
    string? Error,
    long? Threshold,
    string? ClusterSlug);

public static class WikiPageFrontmatterReader
{
    static readonly Regex BlockRx =
        new(@"^---\r?\n(.*?)\r?\n---\r?\n", RegexOptions.Singleline | RegexOptions.Compiled);

    public static WikiPageFrontmatter? Parse(string pageFilePath)
    {
        if (!File.Exists(pageFilePath)) return null;
        string text;
        try { text = File.ReadAllText(pageFilePath); }
        catch { return null; }

        var m = BlockRx.Match(text);
        if (!m.Success) return null;
        return ParseBody(m.Groups[1].Value);
    }

    public static WikiPageFrontmatter ParseBody(string body)
    {
        return new WikiPageFrontmatter(
            SourcePath: ReadString(body, "source_path"),
            SourceTreeSha: ReadBareToken(body, "source_tree_sha"),
            Status: ReadBareToken(body, "status"),
            SynthesisSummary: ReadString(body, "synthesis_summary"),
            GeneratedAt: ReadBareToken(body, "generated_at"),
            SourceFilesCount: ReadInt(body, "source_files_count"),
            SourceBytes: ReadLong(body, "source_bytes"),
            ResearchId: ReadBareToken(body, "research_id"),
            Mode: ReadBareToken(body, "mode"),
            Model: ReadBareToken(body, "model"),
            WorktreeDirty: ReadBool(body, "worktree_dirty"),
            Error: ReadString(body, "error"),
            Threshold: ReadLong(body, "threshold"),
            ClusterSlug: ReadBareToken(body, "cluster_slug"));
    }

    static string? ReadBareToken(string body, string key)
    {
        var m = Regex.Match(body, $@"^{Regex.Escape(key)}:\s*(\S+)\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    // Reads either a bare token or a double-quoted string. Doesn't handle
    // multi-line YAML strings or block scalars — wiki pages don't emit those.
    static string? ReadString(string body, string key)
    {
        var quoted = Regex.Match(body, $@"^{Regex.Escape(key)}:\s*""((?:[^""\\]|\\.)*)""\s*$", RegexOptions.Multiline);
        if (quoted.Success)
            return Unescape(quoted.Groups[1].Value);
        var bare = Regex.Match(body, $@"^{Regex.Escape(key)}:\s*(.+?)\s*$", RegexOptions.Multiline);
        return bare.Success ? bare.Groups[1].Value : null;
    }

    static int? ReadInt(string body, string key)
        => int.TryParse(ReadBareToken(body, key), out var n) ? n : null;

    static long? ReadLong(string body, string key)
        => long.TryParse(ReadBareToken(body, key), out var n) ? n : null;

    static bool? ReadBool(string body, string key) => ReadBareToken(body, key) switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    static string Unescape(string s)
        => s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
}
