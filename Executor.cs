using Microsoft.Extensions.AI;

namespace McpClanker;

// The recursive tool-call loop. Invoke the model, dispatch any tool calls it
// makes, append results to history, repeat. Terminate when the model stops
// calling tools or the tool-call budget is hit.
//
// Deliberately minimal for v1 — no doom-loop detector, no whitelist, no
// retry, no closeout. Those land in v1.5 and v2 once this end-to-end works.

public static class Executor
{
    public static async Task<BuildResult> RunAsync(
        IChatClient chat,
        Contract contract,
        string workingDirectory,
        string branch,
        int maxToolCalls,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var state = new ExecutorState();
        var tools = Tools.Create(workingDirectory, state);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt(contract)),
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

            ChatResponse response;
            try
            {
                response = await chat.GetResponseAsync(history, options, ct);
            }
            catch (Exception ex)
            {
                terminal = TerminalState.Blocked;
                blocked = new BlockedQuestion(
                    BlockedCategory.TransientRetry,
                    $"Chat provider call failed: {ex.GetType().Name}: {ex.Message}",
                    null);
                break;
            }

            foreach (var m in response.Messages)
                history.Add(m);

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

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
                var result = await InvokeTool(tools, call, ct);
                results.Add(new FunctionResultContent(call.CallId, result));
                state.ToolCallCount++;
            }

            history.Add(new ChatMessage(ChatRole.Tool, results));
        }

        var filesChanged = state.FilesTouched
            .Select(kv => new FileChange(kv.Key, kv.Value))
            .ToList();

        return new BuildResult(
            TaskId: contract.TaskId,
            TerminalState: terminal,
            StartedAt: startedAt,
            CompletedAt: DateTime.UtcNow,
            ToolCallCount: state.ToolCallCount,
            RetryCount: 0,
            FilesChanged: filesChanged,
            Tests: null,
            Acceptance: Array.Empty<AcceptanceCheck>(),
            SubAgentsSpawned: Array.Empty<SubAgentResult>(),
            Notes: notes,
            BlockedQuestion: blocked,
            RejectionReason: null,
            WorktreePath: workingDirectory,
            Branch: branch);
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

    static string BuildSystemPrompt(Contract contract) => $@"You are an autonomous coding executor. A contract is supplied below. Your job is to complete it using the tools provided. When you believe the work is done, stop calling tools and write a short note (1-3 sentences) about what you did and any surprises worth surfacing.

Tools available:
- bash: run a shell command in the working directory
- read_file: read a text file relative to the working directory
- write_file: create or overwrite a file relative to the working directory

Rules:
- Stay within the files listed in **Scope:**. Do not touch other files.
- Do not attempt anything listed in **Non-goals:**.
- Paths are relative to the working directory (the contract's git worktree).
- If you genuinely cannot proceed, stop calling tools and explain why in your final message.

=== Contract ===

{contract.RawMarkdown}

=== Begin ===";
}
