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
        int maxOutputTokens,
        string traceDirectory,
        SandboxConfig sandbox,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var state = new ExecutorState
        {
            AllowedNetwork = contract.AllowedNetwork,
        };
        var tools = Tools.Create(workingDirectory, state, sandbox);
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
        // Caller passes the per-provider value from appsettings; cheap
        // executors want high ceilings, premium models want conservative ones.
        var options = new ChatOptions
        {
            MaxOutputTokens = maxOutputTokens,
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
                        chat, history, contract, state, trace, turnCount, maxOutputTokens, ct);
                    tokensIn += selfCheckTokensIn;
                    tokensOut += selfCheckTokensOut;
                    turnCount++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[mcp-clanker] self-check phase failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Closeout phase (v2-plan Phase 5): independent verification.
            // Fresh context, diff-only input, read-only tools, same model.
            // Overrides the self-report's acceptance[] and, if any verdict
            // is fail, demotes the terminal state to failure. See
            // RunCloseoutAsync for the full flow. Only runs on success —
            // nothing to verify on other terminal states.
            if (terminal == TerminalState.Success)
            {
                try
                {
                    var (closeoutTokensIn, closeoutTokensOut, closeoutTurns) =
                        await RunCloseoutAsync(chat, contract, state, workingDirectory, trace, turnCount, maxOutputTokens, ct);
                    tokensIn += closeoutTokensIn;
                    tokensOut += closeoutTokensOut;
                    turnCount += closeoutTurns;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[mcp-clanker] closeout phase failed: {ex.GetType().Name}: {ex.Message}");
                }

                // Apply closeout's demotion BEFORE the finally block writes
                // the end event — otherwise the trace records pre-demotion
                // terminal_state and the rendered transcript shows "success"
                // while the POW reports "failure." Keep the decision here so
                // trace / transcript / POW all agree on the final state.
                if (state.CloseoutReports is { Count: > 0 }
                    && state.CloseoutReports.Any(r => ParseAcceptanceStatus(r.Status) == AcceptanceStatus.Fail))
                {
                    var failed = state.CloseoutReports.Count(r => ParseAcceptanceStatus(r.Status) == AcceptanceStatus.Fail);
                    var total = state.CloseoutReports.Count;
                    terminal = TerminalState.Failure;
                    var demotionNote = $"Closeout verdict: {failed} of {total} acceptance items failed independent verification. See acceptance[] and sub_agents_spawned[] for details.";
                    notes = string.IsNullOrEmpty(notes) ? demotionNote : $"{notes}\n\n{demotionNote}";
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

        // Closeout result OVERRIDES self-report acceptance when present.
        // Self-report remains in the trace for forensics but doesn't make
        // it to the POW JSON — independent verification is authoritative.
        // Terminal demotion on closeout-fail already happened inline above
        // so trace / transcript / POW agree on terminal_state.
        var acceptanceSource = state.CloseoutReports ?? state.AcceptanceReports;
        var acceptance = BuildAcceptanceChecks(acceptanceSource);
        var subAgents = BuildSubAgentResults(state.CloseoutReports, state.CloseoutNotes);

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
            SubAgentsSpawned: subAgents,
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
        int maxOutputTokens,
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
            MaxOutputTokens = maxOutputTokens,
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

    // v2-plan Phase 5: closeout reviewer. Fresh-context sub-agent with
    // read-only tools (read_file / grep / list_dir) and a closeout-scoped
    // finish_work that writes to state.CloseoutReports. The reviewer is
    // handed the contract's acceptance bullets, the executor's self-report
    // for reference, and the worktree diff. Runs its own bounded tool-call
    // loop until it calls finish_work or hits the budget.
    //
    // Same-model-by-default per v2-plan — reuses the passed-in IChatClient.
    // A future extension could route closeout to a different model; for v1
    // cost-efficiency we pick the same one.
    static async Task<(long TokensIn, long TokensOut, int Turns)> RunCloseoutAsync(
        IChatClient chat,
        Contract contract,
        ExecutorState state,
        string workingDirectory,
        TraceWriter trace,
        int priorTurnCount,
        int maxOutputTokens,
        CancellationToken ct)
    {
        const int CloseoutToolBudget = 20;

        var diff = CaptureWorktreeDiff(workingDirectory);

        var readOnlyTools = Tools.CreateReadOnly(workingDirectory);
        var closeoutFinish = AIFunctionFactory.Create(
            (List<AcceptanceReport> reports, string? notes) =>
            {
                state.CloseoutReports = reports;
                state.CloseoutNotes = notes;
                return $"Recorded {reports.Count} closeout verdict(s). Review complete.";
            },
            name: "finish_work",
            description: """
                Record your independent verdicts for the contract's Acceptance bullets
                and complete the review. Call exactly once when you are confident in
                each verdict.
                  - reports: one entry per Acceptance bullet with { item, status, citation }.
                    status is "pass" | "fail" | "unknown".
                    citation MUST anchor in the current worktree state: a file:line from
                    read_file output, a grep match, or a diff hunk. Do NOT cite the
                    executor's own tool calls — verify independently.
                  - notes: optional brief summary of the review (patterns, concerns,
                    anything a parent should know that doesn't fit in an individual
                    citation). Plain prose.
                """);

        var tools = new List<AITool>(readOnlyTools) { closeoutFinish };

        var options = new ChatOptions
        {
            MaxOutputTokens = maxOutputTokens,
            Tools = tools,
            // Not RequireAny — model may need several read_file / grep calls
            // before it's ready to call finish_work. Rely on the prompt and
            // the budget cap.
        };

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.LoadCloseoutPrompt()),
            new(ChatRole.User, BuildCloseoutPrompt(contract, state.AcceptanceReports, diff)),
        };

        long tokensIn = 0;
        long tokensOut = 0;
        int turns = 0;
        int toolCalls = 0;

        while (true)
        {
            if (toolCalls >= CloseoutToolBudget)
            {
                Console.Error.WriteLine($"[mcp-clanker] closeout hit tool-call budget ({CloseoutToolBudget}) without calling finish_work.");
                break;
            }

            turns++;
            var turnNumber = priorTurnCount + turns;
            var turnStart = Stopwatch.GetTimestamp();
            ChatResponse response;
            try
            {
                response = await chat.GetResponseAsync(history, options, ct);
            }
            catch (Exception ex)
            {
                var turnMs = (long)Stopwatch.GetElapsedTime(turnStart).TotalMilliseconds;
                trace.WriteTurn(turnNumber, turnMs, null, null, $"exception:{ex.GetType().Name}", false, null);
                throw;
            }
            var turnDuration = (long)Stopwatch.GetElapsedTime(turnStart).TotalMilliseconds;

            tokensIn += response.Usage?.InputTokenCount ?? 0;
            tokensOut += response.Usage?.OutputTokenCount ?? 0;

            foreach (var m in response.Messages) history.Add(m);

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            trace.WriteTurn(
                turnNumber,
                turnDuration,
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount,
                response.FinishReason?.ToString(),
                hadToolCalls: calls.Count > 0,
                text: response.Text);

            if (calls.Count == 0)
            {
                // Model finished without calling finish_work. Try once to nudge.
                if (state.CloseoutReports is null)
                {
                    history.Add(new ChatMessage(ChatRole.User,
                        "You haven't called finish_work yet. Call it now with your per-bullet verdicts. This is your only remaining action."));
                    continue;
                }
                break;
            }

            var results = new List<AIContent>();
            foreach (var call in calls)
            {
                if (toolCalls >= CloseoutToolBudget) break;

                var callStart = Stopwatch.GetTimestamp();
                var result = await InvokeTool(tools, call, ct);
                var callMs = (long)Stopwatch.GetElapsedTime(callStart).TotalMilliseconds;
                var isError = result.StartsWith("ERROR:", StringComparison.Ordinal);

                trace.WriteToolCall(
                    turn: turnNumber,
                    callId: call.CallId,
                    name: call.Name,
                    args: call.Arguments,
                    success: !isError,
                    durationMs: callMs,
                    resultPreview: TraceWriter.Preview(result, isError: false),
                    error: isError ? TraceWriter.Preview(result, isError: true) : null);

                results.Add(new FunctionResultContent(call.CallId, result));
                toolCalls++;
                state.ToolCallCount++;
            }

            history.Add(new ChatMessage(ChatRole.Tool, results));

            if (state.CloseoutReports is not null)
                break;
        }

        return (tokensIn, tokensOut, turns);
    }

    static string BuildCloseoutPrompt(Contract contract, List<AcceptanceReport>? selfReport, string diff)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Contract goal");
        sb.AppendLine(contract.Goal);
        sb.AppendLine();
        sb.AppendLine("## Acceptance bullets (verify each)");
        for (int i = 0; i < contract.Acceptance.Count; i++)
            sb.Append(i + 1).Append(". ").AppendLine(contract.Acceptance[i]);
        sb.AppendLine();

        if (selfReport is { Count: > 0 })
        {
            sb.AppendLine("## Executor's self-report (for reference only — verify independently)");
            foreach (var r in selfReport)
            {
                sb.Append("- [").Append(r.Status).Append("] ").AppendLine(r.Item);
                if (!string.IsNullOrWhiteSpace(r.Citation))
                    sb.Append("  cited: ").AppendLine(r.Citation);
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Diff of uncommitted changes in the worktree");
        sb.AppendLine("```diff");
        sb.AppendLine(string.IsNullOrWhiteSpace(diff) ? "(empty diff — no changes detected)" : diff.TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Review the diff against each Acceptance bullet. Use read_file / grep / list_dir to confirm current worktree state where needed. Call finish_work with your verdicts. Each citation must anchor in a fact you can point at (file:line, grep match, diff hunk) — not in the executor's own tool calls.");

        return sb.ToString();
    }

    // Capture executor's uncommitted changes, including new (untracked) files.
    // `git add -N .` marks new files as intent-to-add so `git diff HEAD`
    // sees them. Side effect on the worktree's index is intentional and
    // harmless — the worktree is ephemeral.
    static string CaptureWorktreeDiff(string workingDirectory, int maxBytes = 32 * 1024)
    {
        try
        {
            RunGit(workingDirectory, "add", "-N", ".");
            var diff = RunGit(workingDirectory, "diff", "HEAD");
            if (diff.Length <= maxBytes) return diff;
            return diff[..maxBytes] + $"\n\n[diff truncated at {maxBytes} bytes of {diff.Length} total]\n";
        }
        catch (Exception ex)
        {
            return $"[diff capture failed: {ex.GetType().Name}: {ex.Message}]";
        }
    }

    static string RunGit(string cwd, params string[] args)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {proc.ExitCode}): {stderr.Trim()}");
        return stdout;
    }

    static IReadOnlyList<SubAgentResult> BuildSubAgentResults(List<AcceptanceReport>? closeoutReports, string? closeoutNotes)
    {
        if (closeoutReports is null || closeoutReports.Count == 0)
            return Array.Empty<SubAgentResult>();

        var anyFail = closeoutReports.Any(r => ParseAcceptanceStatus(r.Status) == AcceptanceStatus.Fail);
        var anyUnknown = closeoutReports.Any(r => ParseAcceptanceStatus(r.Status) == AcceptanceStatus.Unknown);
        var verdict = anyFail ? "fail" : (anyUnknown ? "mixed" : "pass");

        return new[]
        {
            new SubAgentResult(
                Role: "closeout",
                Verdict: verdict,
                Notes: closeoutNotes ?? ""),
        };
    }
}
