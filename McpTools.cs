using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace McpClanker;

[McpServerToolType]
public static class McpTools
{
    [McpServerTool, Description("Smoke test: asks the configured provider to reply with a short greeting. Use to verify provider wiring.")]
    public static async Task<string> Ping(IChatClient chat, string? message = null)
    {
        var prompt = message ?? "Reply with exactly the word 'pong' and nothing else.";
        var response = await chat.GetResponseAsync(prompt);
        return response.Text;
    }

    [McpServerTool, Description("ALPHA — under active development. Runs an unverified coding executor; lacks closeout verification, acceptance checking, and sandboxing. Do not invoke unless the operator explicitly names this tool. Output shape and failure modes change without notice.")]
    public static async Task<string> Build(IChatClient chat, string contractPath)
    {
        if (!File.Exists(contractPath))
            return BuildResultJson.Serialize(RejectBuild("T-???", null, null, $"Contract file not found: {contractPath}"));

        var markdown = await File.ReadAllTextAsync(contractPath);
        var contract = ContractParser.Parse(markdown);

        var targetRepo = Directory.GetCurrentDirectory();
        var validation = ContractValidator.Validate(contract, targetRepo);
        if (!validation.IsValid)
            return BuildResultJson.Serialize(RejectBuild(contract.TaskId, null, null, validation.RejectionReason!));

        string worktreePath;
        string branch;
        try
        {
            (worktreePath, branch) = Worktree.Create(targetRepo, contract.TaskId);
        }
        catch (Exception ex)
        {
            return BuildResultJson.Serialize(RejectBuild(contract.TaskId, null, null,
                $"Failed to create git worktree for {contract.TaskId}: {ex.Message}"));
        }

        var result = await Executor.RunAsync(
            chat: chat,
            contract: contract,
            workingDirectory: worktreePath,
            branch: branch,
            maxToolCalls: 500,
            ct: CancellationToken.None);

        return BuildResultJson.Serialize(result);
    }

    static BuildResult RejectBuild(string taskId, string? worktreePath, string? branch, string reason)
    {
        var now = DateTime.UtcNow;
        return new BuildResult(
            TaskId: taskId,
            TerminalState: TerminalState.Rejected,
            StartedAt: now,
            CompletedAt: now,
            ToolCallCount: 0,
            RetryCount: 0,
            FilesChanged: Array.Empty<FileChange>(),
            Tests: null,
            Acceptance: Array.Empty<AcceptanceCheck>(),
            SubAgentsSpawned: Array.Empty<SubAgentResult>(),
            Notes: "",
            BlockedQuestion: null,
            RejectionReason: reason,
            WorktreePath: worktreePath ?? "",
            Branch: branch ?? "");
    }

    [McpServerTool, Description("List task IDs, titles, and states from the contract directory. STUB.")]
    public static string ListTasks()
        => "STUB ListTasks";

    [McpServerTool, Description("Read a contract by task ID. STUB.")]
    public static string GetContract(string taskId)
        => $"STUB GetContract: taskId={taskId}";

    [McpServerTool, Description("Read the full execution log for a task. Use when proof-of-work isn't enough to diagnose. STUB.")]
    public static string GetLog(string taskId)
        => $"STUB GetLog: taskId={taskId}";

    [McpServerTool, Description("Dry-run validation of a contract: structure and scope-file existence. Does not execute. STUB.")]
    public static string ValidateContract(string contractPath)
        => $"STUB ValidateContract: contractPath={contractPath}";

    [McpServerTool, Description("Write a new or revised contract to disk. STUB.")]
    public static string UpdateContract(string contractPath, string content)
        => $"STUB UpdateContract: contractPath={contractPath}, contentLen={content.Length}";
}
