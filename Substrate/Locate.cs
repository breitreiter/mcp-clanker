using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Imp.Infrastructure;
using Microsoft.Extensions.AI;
using OpenAI.Embeddings;

namespace Imp.Substrate;

// Phase 2 of `imp tidy`: given a triaged inbox note, decide whether it
// should UPDATE an existing entry or CREATE a new one. Embeds the note,
// kind-filters the cached substrate, takes top-K, hands them to a
// "locate" LLM call that returns the decision.
//
// Kept separate from the draft step so each LLM call has one job —
// when locate misbehaves we can tell whether the embedding-side
// ranking surfaced the right candidate but the LLM still picked wrong,
// or whether the candidate wasn't even in the top-K.
public static class Locate
{
    public const int DefaultTopK = 5;

    // The amount of body shown per candidate, in characters. Enough to
    // convey topic, cheap enough that 5 candidates fit comfortably in a
    // locate call. Tune up if locate starts missing for content-density
    // reasons.
    public const int CandidatePreviewChars = 600;

    public sealed record Result(
        [property: JsonPropertyName("decision")] string Decision,
        [property: JsonPropertyName("target_path")] string? TargetPath,
        [property: JsonPropertyName("rationale")] string Rationale,
        [property: JsonIgnore] IReadOnlyList<EmbeddingIndex.RankedHit> Candidates);

    public static async Task<Result> LocateAsync(
        IChatClient chat,
        EmbeddingClient embed,
        string repoRoot,
        string kind,
        string noteTitle,
        string noteBody,
        string locateSystemPrompt,
        int topK = DefaultTopK)
    {
        // 1. Build the same embedding input shape used at refresh time:
        //    title prepended, body trimmed. Keeps note ↔ entry similarity
        //    apples-to-apples.
        var queryInput = $"{noteTitle}\n\n{noteBody.Trim()}";
        var queryVec = await Embeddings.EmbedAsync(embed, queryInput);

        // 2. Load cache + rank top-K of same kind.
        var cache = EmbeddingIndex.Load(repoRoot);
        var candidates = EmbeddingIndex.RankTopK(cache, queryVec, kind, topK);

        // 3. Zero candidates → create, no LLM call needed.
        if (candidates.Count == 0)
        {
            return new Result(
                Decision: "create",
                TargetPath: null,
                Rationale: $"no existing entries of kind '{kind}' to match against",
                Candidates: candidates);
        }

        // 4. Hand candidates to the LLM.
        var user = BuildUserMessage(kind, noteTitle, noteBody, candidates, repoRoot);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, locateSystemPrompt),
            new(ChatRole.User, user),
        };
        var resp = await chat.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = 400 });
        var text = resp.Text?.Trim() ?? "";

        var json = ExtractJsonObject(text);
        if (json is null)
        {
            ImpLog.Warn($"Locate: response was not parseable JSON: {Truncate(text, 200)}");
            // Fail open: bias toward create on parse failure rather than
            // forcing an update against a guess. False-merge is silent,
            // false-create is recoverable.
            return new Result(
                Decision: "create",
                TargetPath: null,
                Rationale: "locate response unparseable; defaulting to create",
                Candidates: candidates);
        }

        Result? parsed = null;
        try
        {
            var raw = JsonSerializer.Deserialize<RawLocate>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (raw is not null && !string.IsNullOrEmpty(raw.Decision))
            {
                parsed = new Result(
                    Decision: raw.Decision,
                    TargetPath: raw.TargetPath,
                    Rationale: raw.Rationale ?? "",
                    Candidates: candidates);
            }
        }
        catch (JsonException ex)
        {
            ImpLog.Warn($"Locate: deserialize failed: {ex.Message}");
        }

        if (parsed is null)
        {
            return new Result(
                Decision: "create",
                TargetPath: null,
                Rationale: "locate response invalid; defaulting to create",
                Candidates: candidates);
        }

        // 5. Validate target_path against candidates on update decisions.
        if (string.Equals(parsed.Decision, "update", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(parsed.TargetPath)
                || !candidates.Any(c => string.Equals(c.Entry.Path, parsed.TargetPath, StringComparison.Ordinal)))
            {
                ImpLog.Warn($"Locate: update target_path '{parsed.TargetPath}' not in candidates; falling back to create");
                return new Result(
                    Decision: "create",
                    TargetPath: null,
                    Rationale: $"locate picked update but target_path '{parsed.TargetPath}' wasn't a candidate; defaulting to create",
                    Candidates: candidates);
            }
        }

        return parsed;
    }

    sealed record RawLocate(
        [property: JsonPropertyName("decision")] string? Decision,
        [property: JsonPropertyName("target_path")] string? TargetPath,
        [property: JsonPropertyName("rationale")] string? Rationale);

    static string BuildUserMessage(
        string kind,
        string noteTitle,
        string noteBody,
        IReadOnlyList<EmbeddingIndex.RankedHit> candidates,
        string repoRoot)
    {
        var sb = new StringBuilder();
        sb.Append("## The note\n\n");
        sb.Append($"kind: {kind}\n");
        sb.Append($"title: {noteTitle}\n\n");
        sb.Append("body:\n");
        sb.Append(noteBody.Trim());
        sb.Append("\n\n");

        sb.Append($"## Top candidates (kind={kind}, ranked by similarity)\n\n");
        for (int i = 0; i < candidates.Count; i++)
        {
            var hit = candidates[i];
            sb.Append($"### candidate {i + 1}\n");
            sb.Append($"path: {hit.Entry.Path}\n");
            sb.Append($"similarity: {hit.Similarity:F4}\n");

            // Show the candidate's lifecycle metadata + body preview.
            // We read these fresh from disk rather than caching them —
            // a stale cache vs current frontmatter is a refresh-staleness
            // problem, not a locate problem. Lifecycle fields (state,
            // status, updated) matter because a shipped/superseded
            // entry is a historical record: new work should usually
            // seed its own entry and reference the old one in
            // frontmatter, rather than mutate the historical record.
            var preview = LoadCandidatePreview(Path.Combine(repoRoot, hit.Entry.Path));
            sb.Append($"title: {preview.Title}\n");
            if (!string.IsNullOrEmpty(preview.State)) sb.Append($"state: {preview.State}\n");
            if (!string.IsNullOrEmpty(preview.Status)) sb.Append($"status: {preview.Status}\n");
            if (!string.IsNullOrEmpty(preview.Updated)) sb.Append($"updated: {preview.Updated}\n");
            sb.Append("preview:\n");
            sb.Append(preview.Body);
            sb.Append("\n\n");
        }

        sb.Append("Output a single JSON object as instructed. Be conservative — when in doubt, create.\n");
        return sb.ToString();
    }

    sealed record CandidatePreview(string Title, string? State, string? Status, string? Updated, string Body);

    static CandidatePreview LoadCandidatePreview(string absPath)
    {
        string content;
        try { content = File.ReadAllText(absPath); }
        catch (IOException) { return new CandidatePreview("<unreadable>", null, null, null, ""); }

        var (fm, body) = ParseFrontmatter(content);
        var title = fm.TryGetValue("title", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : Path.GetFileNameWithoutExtension(absPath);

        string? state = fm.TryGetValue("state", out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
        string? status = fm.TryGetValue("status", out var st) && !string.IsNullOrWhiteSpace(st) ? st : null;
        string? updated = fm.TryGetValue("updated", out var u) && !string.IsNullOrWhiteSpace(u) ? u : null;

        var trimmed = body.Trim();
        var bodyPreview = trimmed.Length <= CandidatePreviewChars
            ? trimmed
            : trimmed[..CandidatePreviewChars] + "…";
        return new CandidatePreview(title, state, status, updated, bodyPreview);
    }

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
            var trimmedLine = line.TrimEnd('\r');
            var sep = trimmedLine.IndexOf(':');
            if (sep <= 0) continue;
            var key = trimmedLine[..sep].Trim();
            var value = trimmedLine[(sep + 1)..].Trim();
            if (!fm.ContainsKey(key)) fm[key] = value;
        }

        var bodyStr = rest[(endIdx + 4)..].ToString().TrimStart('\r', '\n');
        return (fm, bodyStr);
    }

    // Be lenient with LLM output: extract first balanced {...} block.
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

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
