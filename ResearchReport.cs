using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Imp;

// Output of a research run. Same envelope across all modes (fs, web, future
// custom modes). Field-basis citation contract — every finding carries one
// or more citations, every citation carries excerpts, the finish_research
// tool rejects findings that miss either.
//
// Two citation kinds ship in v1: `file` (path + line range + git SHA) and
// `url` (url + retrieved_at + content_hash). Both share `excerpts[]`.
// Excerpts make findings auditable without re-fetching, which is the whole
// point of field-basis: the parent agent can verify a claim from the report
// alone, no round-trip required.

public record ResearchReport(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("started_at")] DateTime StartedAt,
    [property: JsonPropertyName("completed_at")] DateTime CompletedAt,
    [property: JsonPropertyName("usage")] ResearchUsage Usage,
    [property: JsonPropertyName("synthesis")] string Synthesis,
    [property: JsonPropertyName("coverage")] ResearchCoverage Coverage,
    [property: JsonPropertyName("findings")] IReadOnlyList<Finding> Findings,
    [property: JsonPropertyName("conflicts")] IReadOnlyList<Conflict> Conflicts,
    [property: JsonPropertyName("follow_ups")] IReadOnlyList<string> FollowUps,
    [property: JsonPropertyName("blocked_questions")] IReadOnlyList<ResearchBlockedQuestion> BlockedQuestions,
    [property: JsonPropertyName("worktree_dirty")] bool? WorktreeDirty,
    [property: JsonPropertyName("git_sha")] string? GitSha);

public record ResearchUsage(
    [property: JsonPropertyName("tool_call_count")] int ToolCallCount,
    [property: JsonPropertyName("tokens_in")] long TokensIn,
    [property: JsonPropertyName("tokens_out")] long TokensOut,
    [property: JsonPropertyName("wall_seconds")] double WallSeconds,
    [property: JsonPropertyName("estimated_cost_usd")] decimal EstimatedCostUsd);

public record ResearchCoverage(
    [property: JsonPropertyName("explored")] IReadOnlyList<string> Explored,
    [property: JsonPropertyName("not_explored")] IReadOnlyList<string> NotExplored,
    [property: JsonPropertyName("gaps")] IReadOnlyList<string> Gaps);

public record Finding(
    [property: JsonPropertyName("claim")] string Claim,
    [property: JsonPropertyName("citations")] IReadOnlyList<Citation> Citations,
    [property: JsonPropertyName("confidence")] FindingConfidence Confidence,
    [property: JsonPropertyName("reasoning")] string Reasoning);

// Citation carries the union of fields from all kinds. `kind` discriminates;
// the finish_research tool enforces the per-kind required fields. Keeping it
// flat (vs polymorphic JSON) so the parent can walk it without configuring a
// type-discriminated converter.
public record Citation(
    [property: JsonPropertyName("kind")] CitationKind Kind,
    [property: JsonPropertyName("excerpts")] IReadOnlyList<string> Excerpts,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("line_start")] int? LineStart = null,
    [property: JsonPropertyName("line_end")] int? LineEnd = null,
    [property: JsonPropertyName("git_sha")] string? GitSha = null,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("retrieved_at")] DateTime? RetrievedAt = null,
    [property: JsonPropertyName("content_hash")] string? ContentHash = null);

public record Conflict(
    [property: JsonPropertyName("claim")] string Claim,
    [property: JsonPropertyName("supporting_findings")] IReadOnlyList<int> SupportingFindings,
    [property: JsonPropertyName("contradicting_findings")] IReadOnlyList<int> ContradictingFindings,
    [property: JsonPropertyName("resolution")] string Resolution,
    [property: JsonPropertyName("reasoning")] string Reasoning);

public record ResearchBlockedQuestion(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("assumed_instead")] string AssumedInstead);

[JsonConverter(typeof(JsonStringEnumConverter<FindingConfidence>))]
public enum FindingConfidence { High, Medium, Low }

[JsonConverter(typeof(JsonStringEnumConverter<CitationKind>))]
public enum CitationKind { File, Url }

// Model-supplied portion of the report — what finish_research's parameters
// bind to. The loop fills the surrounding envelope (mode, question, timing,
// usage, git provenance) after the tool fires.
public record FinishResearchInput(
    string Synthesis,
    ResearchCoverage Coverage,
    List<Finding> Findings,
    List<Conflict>? Conflicts,
    List<string>? FollowUps,
    List<ResearchBlockedQuestion>? BlockedQuestions);

// Mutable sink for the research loop. finish_research, registered fresh per
// run, validates and captures the model-supplied portion into Captured; the
// loop sees Captured != null and terminates. SafetyBreach mirrors
// ExecutorState's pattern (canary-tool tripwires post-v1).
public sealed class ResearchState
{
    public FinishResearchInput? Captured { get; set; }
    public SafetyBreach? SafetyBreach { get; private set; }

    public void FlagSafetyBreach(SafetyBreach breach) => SafetyBreach ??= breach;
}

public static class ResearchReportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        // Citation has many optional fields per kind; null-suppression keeps
        // the report tight and readable for the parent.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ResearchReport report) =>
        JsonSerializer.Serialize(report, Options);
}
