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
    [property: JsonPropertyName("files_changed")] IReadOnlyList<FileChange> FilesChanged,
    [property: JsonPropertyName("tests")] TestsReport? Tests,
    [property: JsonPropertyName("acceptance")] IReadOnlyList<AcceptanceCheck> Acceptance,
    [property: JsonPropertyName("sub_agents_spawned")] IReadOnlyList<SubAgentResult> SubAgentsSpawned,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("blocked_question")] BlockedQuestion? BlockedQuestion,
    [property: JsonPropertyName("rejection_reason")] string? RejectionReason,
    [property: JsonPropertyName("worktree_path")] string WorktreePath,
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("trace_path")] string TracePath);

public record FileChange(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("action")] FileAction Action);

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
    [property: JsonPropertyName("status")] AcceptanceStatus Status);

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
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Serialize(BuildResult result) => JsonSerializer.Serialize(result, Options);
}
