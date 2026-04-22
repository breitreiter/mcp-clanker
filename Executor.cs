using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace McpClanker;

// The recursive tool-call loop. Invoke the model, dispatch any tool calls it
// makes, append results to history, repeat. Terminate when the model stops
// calling tools or the tool-call budget is hit.
//
// Deliberately minimal for v1 — no doom-loop detector, no whitelist, no
// retry, no closeout. Those land in v1.5 and v2 once this end-to-end works.
//
// Emits an append-only JSONL trace to traceDirectory/trace.jsonl as events
// happen. Trace is forensic-grade (read it when proof-of-work isn't enough
// to diagnose), not primary output.

public static class Executor
{
    public static async Task<BuildResult> RunAsync(
        IChatClient chat,
        Contract contract,
        string workingDirectory,
        string branch,
        string? providerName,
        string? modelName,
        int maxToolCalls,
        string traceDirectory,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var state = new ExecutorState();
        var tools = Tools.Create(workingDirectory, state);
        var tracePath = Path.Combine(traceDirectory, "trace.jsonl");

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, McpClanker.Prompts.LoadSystemPrompt(providerName, contract)),
            new(ChatRole.User, "Begin."),
        };

        var options = new ChatOptions
        {
            MaxOutputTokens = 4096,
            Tools = tools,
        };

        TerminalState terminal = TerminalState.Failure;
        string notes = "";
        BlockedQuestion? blocked = null;
        int turnCount = 0;
        long tokensIn = 0;
        long tokensOut = 0;

        using var trace = new TraceWriter(tracePath);
        trace.WriteStart(contract.TaskId, providerName, workingDirectory, branch);

        try
        {
            while (true)
            {
                if (state.ToolCallCount >= maxToolCalls)
                {
                    terminal = TerminalState.Blocked;
                    blocked = new BlockedQuestion(
                        BlockedCategory.Abandon,
                        $"Tool-call budget ({maxToolCalls}) exhausted.",
                        null);
                    break;
                }

                turnCount++;
                var turnStart = Stopwatch.GetTimestamp();
                ChatResponse response;
                try
                {
                    response = await chat.GetResponseAsync(history, options, ct);
                }
                catch (Exception ex)
                {
                    var turnMs = (long)Stopwatch.GetElapsedTime(turnStart).TotalMilliseconds;
                    trace.WriteTurn(turnCount, turnMs, null, null, $"exception:{ex.GetType().Name}", false);
                    terminal = TerminalState.Blocked;
                    blocked = new BlockedQuestion(
                        BlockedCategory.TransientRetry,
                        $"Chat provider call failed: {ex.GetType().Name}: {ex.Message}",
                        null);
                    break;
                }
                var turnDuration = (long)Stopwatch.GetElapsedTime(turnStart).TotalMilliseconds;

                tokensIn += response.Usage?.InputTokenCount ?? 0;
                tokensOut += response.Usage?.OutputTokenCount ?? 0;

                foreach (var m in response.Messages)
                    history.Add(m);

                var calls = response.Messages
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionCallContent>()
                    .ToList();

                trace.WriteTurn(
                    turnCount,
                    turnDuration,
                    response.Usage?.InputTokenCount,
                    response.Usage?.OutputTokenCount,
                    response.FinishReason?.ToString(),
                    hadToolCalls: calls.Count > 0);

                if (calls.Count == 0)
                {
                    terminal = TerminalState.Success;
                    notes = response.Text ?? "";
                    break;
                }

                var results = new List<AIContent>();
                foreach (var call in calls)
                {
                    if (state.ToolCallCount >= maxToolCalls) break;

                    var callStart = Stopwatch.GetTimestamp();
                    var result = await InvokeTool(tools, call, ct);
                    var callDuration = (long)Stopwatch.GetElapsedTime(callStart).TotalMilliseconds;

                    var isError = result.StartsWith("ERROR:", StringComparison.Ordinal);
                    trace.WriteToolCall(
                        turn: turnCount,
                        callId: call.CallId,
                        name: call.Name,
                        args: call.Arguments,
                        success: !isError,
                        durationMs: callDuration,
                        resultPreview: TraceWriter.Preview(result, isError: false),
                        error: isError ? TraceWriter.Preview(result, isError: true) : null);

                    results.Add(new FunctionResultContent(call.CallId, result));
                    state.ToolCallCount++;
                }

                history.Add(new ChatMessage(ChatRole.Tool, results));
            }
        }
        finally
        {
            trace.WriteEnd(terminal.ToString().ToLowerInvariant(), state.ToolCallCount, turnCount);
        }

        var filesChanged = state.FilesTouched
            .Select(kv => new FileChange(kv.Key, kv.Value))
            .ToList();

        var scopeAdherence = CheckScopeAdherence(contract, filesChanged);
        var estimatedCost = Pricing.Estimate(modelName, tokensIn, tokensOut);

        return new BuildResult(
            TaskId: contract.TaskId,
            TerminalState: terminal,
            StartedAt: startedAt,
            CompletedAt: DateTime.UtcNow,
            ToolCallCount: state.ToolCallCount,
            RetryCount: 0,
            TokensInputTotal: tokensIn,
            TokensOutputTotal: tokensOut,
            EstimatedCostUsd: estimatedCost,
            FilesChanged: filesChanged,
            ScopeAdherence: scopeAdherence,
            Tests: null,
            Acceptance: Array.Empty<AcceptanceCheck>(),
            SubAgentsSpawned: Array.Empty<SubAgentResult>(),
            Notes: notes,
            BlockedQuestion: blocked,
            RejectionReason: null,
            WorktreePath: workingDirectory,
            Branch: branch,
            TracePath: tracePath);
    }

    // Algorithmic scope check: flag any files_changed path that isn't in the
    // contract's declared Scope. Normalization: forward slashes, trimmed.
    // Case-sensitive — git and Linux both are, so a case mismatch is a real
    // deviation, not an alias.
    static ScopeAdherence CheckScopeAdherence(Contract contract, IReadOnlyList<FileChange> filesChanged)
    {
        var declared = contract.Scope.Select(s => Normalize(s.Path)).ToHashSet(StringComparer.Ordinal);
        var outOfScope = filesChanged
            .Where(f => !declared.Contains(Normalize(f.Path)))
            .Select(f => f.Path)
            .ToList();
        return new ScopeAdherence(InScope: outOfScope.Count == 0, OutOfScopePaths: outOfScope);

        static string Normalize(string p) => p.Trim().Replace('\\', '/');
    }

    static async Task<string> InvokeTool(IList<AITool> tools, FunctionCallContent call, CancellationToken ct)
    {
        var tool = tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == call.Name);
        if (tool is null)
            return $"ERROR: unknown tool '{call.Name}'";

        try
        {
            var args = new AIFunctionArguments(call.Arguments);
            var result = await tool.InvokeAsync(args, ct);
            return result?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            return $"ERROR: tool '{call.Name}' threw: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
