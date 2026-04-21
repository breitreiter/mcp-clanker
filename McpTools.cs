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

    [McpServerTool, Description("Run a build against a contract file. Long-running; returns proof-of-work when finished. STUB.")]
    public static string Build(string contractPath)
        => $"STUB Build: contractPath={contractPath}";

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
