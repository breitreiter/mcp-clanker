using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace McpClanker;

// The recursive tool-call loop. Invoke the model, dispatch any tool calls it
// makes, append results to history, repeat. Terminate when the model stops
// calling tools or the tool-call budget is hit.
//
// Deliberately minimal for v1 — no retry, no closeout. Those land in v2.
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
        var transcriptPath = Path.Combine(traceDirectory, "transcript.md");

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, McpClanker.Prompts.LoadSystemPrompt(providerName, contract)),
            new(ChatRole.User, "Begin."),
        };

        // MaxOutputTokens sizing: reasoning models (GPT-5.x, Claude thinking
        // variants) burn a chunk of this budget on hidden reasoning before
        // emitting visible output or tool calls. 4096 was enough for trivial
        // turns but too tight for write_file of a ~200-line source file:
        // phase 2 validation run T-001 hit finish_reason=length on turn 5
        // with 4096 output tokens consumed and zero tool calls emitted.
        // 16384 gives reasoning room + a substantial write payload without
        // materially changing cost on the cheap executor. Config-driven
        // sizing is follow-up work (TODO).
        var options = new ChatOptions
        {
            MaxOutputTokens = 16384,
            Tools = tools,
        };

        TerminalState terminal = TerminalState.Failure;
        string notes = "";
        BlockedQuestion? blocked = null;
        int turnCount = 0;
        long tokensIn = 0;
        long tokensOut = 0;

        using var trace = new TraceWriter(tracePath);
        trace.WriteStart(contract.TaskId, contract.Title, contract.Goal, providerName, workingDirectory, branch);

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
                    trace.WriteTurn(turnCount, turnMs, null, null, $"exception:{ex.GetType().Name}", false, null);
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
                    hadToolCalls: calls.Count > 0,
                    text: response.Text);

                if (calls.Count == 0)
                {
                    // "No tool calls" alone doesn't mean success — the model
                    // can also bail because it hit the output-token ceiling
                    // (finish_reason=length) or because a content filter
                    // tripped. Both look identical in shape but are failures,
                    // not completions. Check the reason before declaring victory.
                    var finishReason = response.FinishReason;
                    if (finishReason == ChatFinishReason.Length)
                    {
                        terminal = TerminalState.Blocked;
                        blocked = new BlockedQuestion(
                            BlockedCategory.RescopeOrCapability,
                            $"Model hit per-turn output-token ceiling ({options.MaxOutputTokens}) at turn {turnCount} without emitting a tool call. Narrow the contract (smaller work per task) or raise MaxOutputTokens.",
                            null);
                        break;
                    }
                    if (finishReason == ChatFinishReason.ContentFilter)
                    {
                        terminal = TerminalState.Blocked;
                        blocked = new BlockedQuestion(
                            BlockedCategory.Abandon,
                            $"Model response blocked by content filter at turn {turnCount}.",
                            null);
                        break;
                    }
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

                    // Record for doom-loop detection. Full serialized args
                    // as the signature (no hashing) — we cap at 10 records,
                    // so memory is trivial and equality is exact.
                    var argsSignature = call.Arguments is null
                        ? "{}"
                        : JsonSerializer.Serialize(call.Arguments);
                    state.RecordToolCall(new ToolCallRecord(call.Name, argsSignature, Success: !isError));
                }

                history.Add(new ChatMessage(ChatRole.Tool, results));

                // Run the doom-loop detector over the most recent calls.
                // Pre-flight gates (CommandClassifier, NetworkEgressChecker)
                // may already have flagged a breach during tool dispatch;
                // only run the loop detector if the batch survived those.
                if (state.SafetyBreach is null)
                {
                    var doomLoop = DoomLoopDetector.Check(state.RecentCalls);
                    if (doomLoop.Tripped)
                    {
                        state.FlagSafetyBreach(new SafetyBreach(
                            Category: BlockedCategory.Abandon,
                            Summary: $"Doom-loop detected: {doomLoop.Reason}.",
                            OffendingInput: doomLoop.OffendingInput ?? ""));
                    }
                }

                // A safety gate (pre-flight or doom-loop) can flag a breach
                // during tool dispatch. When flagged, terminate the whole
                // run as blocked — safety breaches aren't recoverable
                // inside the same contract.
                if (state.SafetyBreach is { } breach)
                {
                    terminal = TerminalState.Blocked;
                    blocked = new BlockedQuestion(
                        Category: breach.Category,
                        Summary: breach.Summary,
                        OffendingInput: breach.OffendingInput);
                    break;
                }
            }

            // Self-check phase: only on clean success. One extra model turn
            // with a finish_work-only tool list, asking the model to report
            // per-bullet verdicts with citations. Populates
            // BuildResult.Acceptance. See v2-plan phase 4. Self-check
            // failure is logged but doesn't demote the terminal state —
            // the main work already ran cleanly.
            if (terminal == TerminalState.Success)
            {
                try
                {
                    var (selfCheckTokensIn, selfCheckTokensOut) = await RunSelfCheckAsync(
                        chat, history, contract, state, trace, turnCount, ct);
                    tokensIn += selfCheckTokensIn;
                    tokensOut += selfCheckTokensOut;
                    turnCount++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[mcp-clanker] self-check phase failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            trace.WriteEnd(terminal.ToString().ToLowerInvariant(), state.ToolCallCount, turnCount);
        }

        try
        {
            TranscriptRenderer.Render(tracePath, transcriptPath);
        }
        catch (Exception ex)
        {
            // Transcript is best-effort; JSONL is the authoritative record.
            Console.Error.WriteLine($"[mcp-clanker] transcript render failed: {ex.GetType().Name}: {ex.Message}");
        }

        var filesChanged = state.FilesTouched
            .Select(kv => new FileChange(kv.Key, kv.Value))
            .ToList();

        var scopeAdherence = CheckScopeAdherence(contract, filesChanged);
        var estimatedCost = Pricing.Estimate(modelName, tokensIn, tokensOut);
        var acceptance = BuildAcceptanceChecks(state.AcceptanceReports);

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
            Acceptance: acceptance,
            SubAgentsSpawned: Array.Empty<SubAgentResult>(),
            Notes: notes,
            BlockedQuestion: blocked,
            RejectionReason: null,
            WorktreePath: workingDirectory,
            Branch: branch,
            TracePath: tracePath,
            TranscriptPath: transcriptPath);
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

    // One-turn self-check. Called after the main loop terminates with success.
    // Exposes only the `finish_work` tool; the model is asked to call it once
    // with per-bullet verdicts and citations. Returns (tokensIn, tokensOut)
    // so the outer method can roll them into the run totals. The collected
    // reports land on ExecutorState.AcceptanceReports — callers read from
    // there rather than from this method's return value.
    static async Task<(long TokensIn, long TokensOut)> RunSelfCheckAsync(
        IChatClient chat,
        List<ChatMessage> history,
        Contract contract,
        ExecutorState state,
        TraceWriter trace,
        int turnCount,
        CancellationToken ct)
    {
        var finishWork = AIFunctionFactory.Create(
            (List<AcceptanceReport> reports) =>
            {
                state.AcceptanceReports = reports;
                return $"Recorded {reports.Count} acceptance report(s).";
            },
            name: "finish_work",
            description: """
                Record verdicts for the contract's Acceptance bullets and terminate the run.
                Call exactly once with `reports`, one entry per Acceptance bullet:
                  - item: the acceptance bullet verbatim (or lightly summarized).
                  - status: "pass" | "fail" | "unknown".
                  - citation: a specific file:line, tool-call summary, or diff reference
                    that justifies the status. "I believe so" is not a valid citation.
                """);

        var options = new ChatOptions
        {
            MaxOutputTokens = 16384,
            Tools = new List<AITool> { finishWork },
            ToolMode = ChatToolMode.RequireAny,
        };

        var prompt = BuildSelfCheckPrompt(contract.Acceptance);
        history.Add(new ChatMessage(ChatRole.User, prompt));

        var selfTurn = turnCount + 1;
        var started = Stopwatch.GetTimestamp();
        ChatResponse response;
        try
        {
            response = await chat.GetResponseAsync(history, options, ct);
        }
        catch (Exception ex)
        {
            var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            trace.WriteTurn(selfTurn, ms, null, null, $"exception:{ex.GetType().Name}", false, null);
            throw;
        }

        var duration = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        foreach (var m in response.Messages)
            history.Add(m);

        var calls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .ToList();

        trace.WriteTurn(
            selfTurn,
            duration,
            response.Usage?.InputTokenCount,
            response.Usage?.OutputTokenCount,
            response.FinishReason?.ToString(),
            hadToolCalls: calls.Count > 0,
            text: response.Text);

        foreach (var call in calls)
        {
            var callStart = Stopwatch.GetTimestamp();
            var result = await InvokeTool(options.Tools!, call, ct);
            var callMs = (long)Stopwatch.GetElapsedTime(callStart).TotalMilliseconds;
            var isError = result.StartsWith("ERROR:", StringComparison.Ordinal);
            trace.WriteToolCall(
                turn: selfTurn,
                callId: call.CallId,
                name: call.Name,
                args: call.Arguments,
                success: !isError,
                durationMs: callMs,
                resultPreview: TraceWriter.Preview(result, isError: false),
                error: isError ? TraceWriter.Preview(result, isError: true) : null);
            state.ToolCallCount++;
        }

        return (response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0);
    }

    static string BuildSelfCheckPrompt(IReadOnlyList<string> acceptance)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Execution is complete. Before we finish, verify the contract's Acceptance bullets.");
        sb.AppendLine();
        sb.AppendLine("Call `finish_work` exactly once with a `reports` array — one entry per bullet below. Each report needs:");
        sb.AppendLine("  - item: the bullet text (verbatim or lightly summarized).");
        sb.AppendLine("  - status: \"pass\" | \"fail\" | \"unknown\".");
        sb.AppendLine("  - citation: a specific file:line, tool-call summary, or diff reference. Not \"I believe so\".");
        sb.AppendLine();
        sb.AppendLine("Acceptance bullets:");
        for (int i = 0; i < acceptance.Count; i++)
            sb.Append("  ").Append(i + 1).Append(". ").AppendLine(acceptance[i]);
        sb.AppendLine();
        sb.AppendLine("Call finish_work now. Do not call any other tool.");
        return sb.ToString();
    }

    static IReadOnlyList<AcceptanceCheck> BuildAcceptanceChecks(List<AcceptanceReport>? reports)
    {
        if (reports is null) return Array.Empty<AcceptanceCheck>();
        return reports
            .Select(r => new AcceptanceCheck(
                Item: r.Item,
                Status: ParseAcceptanceStatus(r.Status),
                Citation: r.Citation ?? ""))
            .ToList();
    }

    static AcceptanceStatus ParseAcceptanceStatus(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "pass" or "passed" or "ok" or "true" => AcceptanceStatus.Pass,
        "fail" or "failed" or "false" => AcceptanceStatus.Fail,
        _ => AcceptanceStatus.Unknown,
    };
}
