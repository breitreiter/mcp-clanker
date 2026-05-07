using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Imp;

// Tools specific to research mode. Today: just finish_research, the
// terminal-action tool every mode registers. As web mode lands, this file
// grows to hold web_search / http_get / extract_text and any other
// reach-typed surfaces that aren't already in the file-tool set.

public static class ResearchTools
{
    // The finish_research factory, referenced by every ModeDefinition's
    // FinishToolFactory. Closes over the per-run ResearchState so the tool
    // can capture the validated input and signal the loop to terminate.
    //
    // Validation is enforced here, not in the model prompt: the prompt
    // describes the contract; the tool refuses bad input with an error
    // string the model can read and retry against. The validation rules
    // are the field-basis citation contract:
    //   - findings array is non-empty
    //   - every finding has at least one citation
    //   - every citation has at least one non-empty excerpt
    //   - per-kind required fields (file: path + line range; url: url)
    //   - confidence is one of the enum values (handled by the converter)
    public static AIFunction BuildFinishResearchTool(ResearchState state) =>
        AIFunctionFactory.Create(
            (
                [Description("One-paragraph direct answer. No 'I found that...' framing — state the conclusion.")] string synthesis,
                [Description("What was looked at, what wasn't, where gaps remain.")] ResearchCoverage coverage,
                [Description("Findings that back up the synthesis. At least one. Each finding: claim, citations[] (each with excerpts[]), confidence (high/medium/low), reasoning.")] List<Finding> findings,
                [Description("Optional. Conflicts between findings, with supporting/contradicting indices into the findings array, a chosen resolution, and reasoning.")] List<Conflict>? conflicts = null,
                [Description("Optional. Open questions for the parent to consider issuing as a follow-up research run.")] List<string>? follow_ups = null,
                [Description("Optional. Questions that couldn't be answered without clarification, with the assumption made instead.")] List<ResearchBlockedQuestion>? blocked_questions = null) =>
            {
                var input = new FinishResearchInput(
                    Synthesis: synthesis ?? "",
                    Coverage: coverage ?? new ResearchCoverage(
                        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                    Findings: findings ?? new List<Finding>(),
                    Conflicts: conflicts,
                    FollowUps: follow_ups,
                    BlockedQuestions: blocked_questions);

                var error = Validate(input);
                if (error is not null)
                    return $"ERROR: finish_research input rejected — {error}. Adjust and call again.";

                state.Captured = input;
                return $"Recorded research report ({input.Findings.Count} finding(s)).";
            },
            name: "finish_research",
            description: """
                Record the final research report and terminate the run. Call exactly
                once when you have enough evidence to answer the question.

                Validation rules (failures return an error you can retry against):
                  - findings array must be non-empty.
                  - every finding must have non-empty claim and reasoning.
                  - every finding must have at least one citation.
                  - every citation must have at least one non-empty excerpt.
                  - citation kind="file" requires path + line_start + line_end.
                  - citation kind="url" requires url.
                  - confidence must be one of: high, medium, low.

                Excerpts make findings auditable without re-fetching — the parent
                agent verifies your claims from the report alone, no round-trip.
                Quote enough text that the citation stands on its own.
                """);

    static string? Validate(FinishResearchInput input)
    {
        if (input.Findings is null || input.Findings.Count == 0)
            return "findings[] is empty; at least one finding is required";

        for (int i = 0; i < input.Findings.Count; i++)
        {
            var f = input.Findings[i];
            if (string.IsNullOrWhiteSpace(f.Claim))
                return $"finding[{i}].claim is empty";
            if (string.IsNullOrWhiteSpace(f.Reasoning))
                return $"finding[{i}].reasoning is empty; every finding needs one sentence on why the citations support the claim";
            if (f.Citations is null || f.Citations.Count == 0)
                return $"finding[{i}] has no citations; every finding needs at least one";

            for (int j = 0; j < f.Citations.Count; j++)
            {
                var c = f.Citations[j];
                if (c.Excerpts is null || c.Excerpts.Count == 0
                    || c.Excerpts.All(string.IsNullOrWhiteSpace))
                    return $"finding[{i}].citations[{j}] has no non-empty excerpts";

                switch (c.Kind)
                {
                    case CitationKind.File:
                        if (string.IsNullOrWhiteSpace(c.Path))
                            return $"finding[{i}].citations[{j}] kind=file requires path";
                        if (c.LineStart is null || c.LineEnd is null)
                            return $"finding[{i}].citations[{j}] kind=file requires line_start and line_end";
                        if (c.LineStart > c.LineEnd)
                            return $"finding[{i}].citations[{j}] line_start ({c.LineStart}) > line_end ({c.LineEnd})";
                        break;
                    case CitationKind.Url:
                        if (string.IsNullOrWhiteSpace(c.Url))
                            return $"finding[{i}].citations[{j}] kind=url requires url";
                        break;
                }
            }
        }
        return null;
    }
}
