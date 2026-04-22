using System.Globalization;
using System.Text;
using System.Text.Json;

namespace McpClanker;

// Reads a JSONL trace (written by TraceWriter) and emits a human-readable
// markdown transcript next to it. Best-effort — malformed or unexpected
// events are skipped rather than crashing. JSONL is authoritative; the
// transcript is a convenience view that can be regenerated at any time.

public static class TranscriptRenderer
{
    public static void Render(string tracePath, string outputPath)
    {
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"Trace file not found: {tracePath}", tracePath);

        StartEvent? start = null;
        EndEvent? end = null;
        var turns = new SortedDictionary<int, TurnEvent>();
        var toolCalls = new List<ToolCallEvent>();

        foreach (var line in File.ReadLines(tracePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(line);
                root = doc.RootElement.Clone();
            }
            catch
            {
                continue;
            }

            var type = GetString(root, "type");
            switch (type)
            {
                case "start":
                    start = StartEvent.FromJson(root);
                    break;
                case "turn":
                    var t = TurnEvent.FromJson(root);
                    if (t is not null) turns[t.N] = t;
                    break;
                case "tool_call":
                    var tc = ToolCallEvent.FromJson(root);
                    if (tc is not null) toolCalls.Add(tc);
                    break;
                case "end":
                    end = EndEvent.FromJson(root);
                    break;
            }
        }

        var md = BuildMarkdown(start, turns, toolCalls, end);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, md);
    }

    static string BuildMarkdown(
        StartEvent? start,
        SortedDictionary<int, TurnEvent> turns,
        List<ToolCallEvent> toolCalls,
        EndEvent? end)
    {
        var sb = new StringBuilder();
        WriteHeader(sb, start, turns, end);

        var callsByTurn = toolCalls
            .GroupBy(c => c.Turn)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (_, turn) in turns)
        {
            var calls = callsByTurn.TryGetValue(turn.N, out var list) ? list : new List<ToolCallEvent>();
            WriteTurn(sb, turn, calls);
        }

        return sb.ToString();
    }

    static void WriteHeader(
        StringBuilder sb,
        StartEvent? start,
        SortedDictionary<int, TurnEvent> turns,
        EndEvent? end)
    {
        var taskId = start?.TaskId ?? "T-???";
        var title = start?.Title ?? "(no title)";
        sb.Append("# ").Append(taskId).Append(" — ").AppendLine(title);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(start?.Goal))
        {
            sb.Append("**Goal:** ").AppendLine(start!.Goal);
            sb.AppendLine();
        }

        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");

        if (start is not null)
            sb.Append("| Started | ").Append(FormatTimestamp(start.Timestamp)).AppendLine(" |");

        if (end is not null)
        {
            sb.Append("| Completed | ").Append(FormatTimestamp(end.Timestamp)).AppendLine(" |");
            if (start is not null)
                sb.Append("| Duration | ").Append(FormatDuration(end.Timestamp - start.Timestamp)).AppendLine(" |");
            sb.Append("| Terminal state | **").Append(end.TerminalState ?? "unknown").AppendLine("** |");
        }

        if (!string.IsNullOrEmpty(start?.Provider))
            sb.Append("| Provider | ").Append(start!.Provider).AppendLine(" |");
        if (!string.IsNullOrEmpty(start?.WorktreePath))
            sb.Append("| Worktree | `").Append(start!.WorktreePath).AppendLine("` |");
        if (!string.IsNullOrEmpty(start?.Branch))
            sb.Append("| Branch | `").Append(start!.Branch).AppendLine("` |");

        sb.Append("| Turns | ").Append(turns.Count).AppendLine(" |");
        if (end is not null)
            sb.Append("| Tool calls | ").Append(end.ToolCallCount).AppendLine(" |");

        long totalIn = turns.Values.Sum(x => x.TokensIn ?? 0);
        long totalOut = turns.Values.Sum(x => x.TokensOut ?? 0);
        if (totalIn > 0 || totalOut > 0)
            sb.Append("| Tokens | ").Append(totalIn.ToString("N0")).Append(" in / ")
              .Append(totalOut.ToString("N0")).AppendLine(" out |");

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
    }

    static void WriteTurn(StringBuilder sb, TurnEvent turn, List<ToolCallEvent> calls)
    {
        sb.Append("## Turn ").Append(turn.N);
        sb.Append(" — ").Append(FormatDurationMs(turn.DurationMs));
        if (!string.IsNullOrEmpty(turn.FinishReason))
            sb.Append(" — finish_reason: `").Append(turn.FinishReason).Append('`');
        if (turn.TokensIn.HasValue || turn.TokensOut.HasValue)
            sb.Append(" — ").Append((turn.TokensIn ?? 0).ToString("N0")).Append(" in / ")
              .Append((turn.TokensOut ?? 0).ToString("N0")).Append(" out");
        sb.AppendLine();
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(turn.Text))
        {
            foreach (var line in turn.Text!.Split('\n'))
                sb.Append("> ").AppendLine(line.TrimEnd('\r'));
            sb.AppendLine();
        }

        foreach (var call in calls)
            WriteToolCall(sb, call);

        if (calls.Count == 0 && string.IsNullOrWhiteSpace(turn.Text))
            sb.AppendLine("_(no output)_").AppendLine();
    }

    static void WriteToolCall(StringBuilder sb, ToolCallEvent call)
    {
        sb.Append("**").Append(call.Name).Append("** — ").Append(FormatDurationMs(call.DurationMs));
        if (!call.Success) sb.Append(" — **failed**");
        if (call.Args is null && !string.IsNullOrEmpty(call.ArgsHash))
            sb.Append(" — `args#").Append(call.ArgsHash).Append('`');
        sb.AppendLine();
        sb.AppendLine();

        if (!string.IsNullOrEmpty(call.Args))
        {
            sb.AppendLine("```json");
            sb.AppendLine(call.Args);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(call.Error))
        {
            sb.AppendLine("_error:_");
            sb.AppendLine("```");
            sb.AppendLine(call.Error);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        else if (!string.IsNullOrEmpty(call.ResultPreview))
        {
            sb.AppendLine("_result:_");
            sb.AppendLine("```");
            sb.AppendLine(call.ResultPreview);
            sb.AppendLine("```");
            sb.AppendLine();
        }
    }

    static string FormatTimestamp(DateTime t)
        => t.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    static string FormatDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 1) return $"{d.TotalMilliseconds:N0}ms";
        if (d.TotalMinutes < 1) return $"{d.TotalSeconds:N1}s";
        return $"{(int)d.TotalMinutes}m {d.Seconds}s";
    }

    static string FormatDurationMs(long ms)
    {
        if (ms < 1000) return $"{ms}ms";
        if (ms < 60_000) return $"{ms / 1000.0:N1}s";
        return $"{ms / 60_000}m {ms % 60_000 / 1000}s";
    }

    static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long? GetLong(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    static bool GetBool(JsonElement el, string prop, bool fallback = false)
        => el.TryGetProperty(prop, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : fallback;

    static int GetInt(JsonElement el, string prop, int fallback = 0)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    static DateTime GetTimestamp(JsonElement el)
        => el.TryGetProperty("timestamp", out var v)
            && v.ValueKind == JsonValueKind.String
            && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUniversalTime()
            : DateTime.MinValue;

    sealed record StartEvent(
        DateTime Timestamp, string? TaskId, string? Title, string? Goal,
        string? Provider, string? WorktreePath, string? Branch)
    {
        public static StartEvent FromJson(JsonElement el) => new(
            Timestamp: GetTimestamp(el),
            TaskId: GetString(el, "task_id"),
            Title: GetString(el, "title"),
            Goal: GetString(el, "goal"),
            Provider: GetString(el, "provider"),
            WorktreePath: GetString(el, "worktree_path"),
            Branch: GetString(el, "branch"));
    }

    sealed record TurnEvent(
        int N, long DurationMs, long? TokensIn, long? TokensOut,
        string? FinishReason, bool HadToolCalls, string? Text)
    {
        public static TurnEvent? FromJson(JsonElement el)
        {
            if (!el.TryGetProperty("n", out var nEl) || !nEl.TryGetInt32(out var n)) return null;
            return new TurnEvent(
                N: n,
                DurationMs: GetLong(el, "duration_ms") ?? 0,
                TokensIn: GetLong(el, "tokens_in"),
                TokensOut: GetLong(el, "tokens_out"),
                FinishReason: GetString(el, "finish_reason"),
                HadToolCalls: GetBool(el, "had_tool_calls"),
                Text: GetString(el, "text"));
        }
    }

    sealed record ToolCallEvent(
        int Turn, string Name, string? ArgsHash, string? Args,
        bool Success, long DurationMs, string? ResultPreview, string? Error)
    {
        public static ToolCallEvent FromJson(JsonElement el) => new(
            Turn: GetInt(el, "turn"),
            Name: GetString(el, "name") ?? "(unknown)",
            ArgsHash: GetString(el, "args_hash"),
            Args: GetString(el, "args"),
            Success: GetBool(el, "success", true),
            DurationMs: GetLong(el, "duration_ms") ?? 0,
            ResultPreview: GetString(el, "result_preview"),
            Error: GetString(el, "error"));
    }

    sealed record EndEvent(
        DateTime Timestamp, string? TerminalState, int ToolCallCount, int TurnCount)
    {
        public static EndEvent FromJson(JsonElement el) => new(
            Timestamp: GetTimestamp(el),
            TerminalState: GetString(el, "terminal_state"),
            ToolCallCount: GetInt(el, "tool_call_count"),
            TurnCount: GetInt(el, "turn_count"));
    }
}
