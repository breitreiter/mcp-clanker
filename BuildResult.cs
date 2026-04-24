using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpClanker;

// Proof-of-work. Shape matches project/BRIEF.md's schema plus two extras
// (worktree_path, branch) so the parent can navigate straight to the work.
// v2-gated fields are present but filled with empty sentinels in v1.

public record BuildResult(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("terminal_state")] TerminalState TerminalState,
    [property: JsonPropertyName("started_at")] DateTime StartedAt,
    [property: JsonPropertyName("completed_at")] DateTime CompletedAt,
    [property: JsonPropertyName("tool_call_count")] int ToolCallCount,
    [property: JsonPropertyName("retry_count")] int RetryCount,
    [property: JsonPropertyName("tokens_input_total")] long TokensInputTotal,
    [property: JsonPropertyName("tokens_output_total")] long TokensOutputTotal,
    [property: JsonPropertyName("estimated_cost_usd")] decimal EstimatedCostUsd,
    [property: JsonPropertyName("files_changed")] IReadOnlyList<FileChange> FilesChanged,
    [property: JsonPropertyName("scope_adherence")] ScopeAdherence ScopeAdherence,
    [property: JsonPropertyName("tests")] TestsReport? Tests,
    [property: JsonPropertyName("acceptance")] IReadOnlyList<AcceptanceCheck> Acceptance,
    [property: JsonPropertyName("sub_agents_spawned")] IReadOnlyList<SubAgentResult> SubAgentsSpawned,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("blocked_question")] BlockedQuestion? BlockedQuestion,
    [property: JsonPropertyName("rejection_reason")] string? RejectionReason,
    [property: JsonPropertyName("worktree_path")] string WorktreePath,
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("trace_path")] string TracePath,
    [property: JsonPropertyName("transcript_path")] string TranscriptPath);

public record FileChange(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("action")] FileAction Action);

public record ScopeAdherence(
    [property: JsonPropertyName("in_scope")] bool InScope,
    [property: JsonPropertyName("out_of_scope_paths")] IReadOnlyList<string> OutOfScopePaths);

public record BlockedQuestion(
    [property: JsonPropertyName("category")] BlockedCategory Category,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("offending_input")] string? OffendingInput);

public record TestsReport(
    [property: JsonPropertyName("added")] IReadOnlyList<string> Added,
    [property: JsonPropertyName("modified")] IReadOnlyList<string> Modified,
    [property: JsonPropertyName("existing_passed")] bool? ExistingPassed);

public record AcceptanceCheck(
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("status")] AcceptanceStatus Status,
    [property: JsonPropertyName("citation")] string Citation);

public record SubAgentResult(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("notes")] string Notes);

[JsonConverter(typeof(JsonStringEnumConverter<TerminalState>))]
public enum TerminalState { Success, Failure, Rejected, Blocked }

[JsonConverter(typeof(JsonStringEnumConverter<FileAction>))]
public enum FileAction { Created, Modified, Deleted }

[JsonConverter(typeof(JsonStringEnumConverter<BlockedCategory>))]
public enum BlockedCategory
{
    ClarifyThenRetry,
    ReviseContract,
    RescopeOrCapability,
    Abandon,
    TransientRetry,
}

[JsonConverter(typeof(JsonStringEnumConverter<AcceptanceStatus>))]
public enum AcceptanceStatus { Pass, Fail, Unknown }

public static class BuildResultJson
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        // Default escaping turns backticks, smart quotes, and other safe-but-non-ASCII
        // characters into \u sequences — fine for browsers, ugly for humans reading
        // proof-of-work in a terminal. Relaxed encoder keeps them as literals.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Serialize(BuildResult result) => JsonSerializer.Serialize(result, Options);
}
