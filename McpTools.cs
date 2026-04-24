using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
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

    [McpServerTool, Description("Executes a contract file through a cheap, slow coding executor (default: Azure GPT-5.1-codex-mini) in a fresh git worktree, and returns a structured proof-of-work JSON. Long-running: minutes to tens of minutes. Use for rote, narrow-scoped tasks with explicit file Scope and 3-6 verifiable Acceptance bullets; don't use for exploration, cross-cutting refactors, or judgment-heavy work. Draft the contract from the `template://contract` resource. Runs with safety gates (danger-pattern / network-egress / doom-loop), a closeout reviewer that independently verifies acceptance on success (`terminal_state=failure` from a success-then-demoted run means the closeout caught something), and an optional Docker sandbox (opt-in via appsettings). **Do not delegate tasks that require adding a package the project hasn't already adopted** — the sandbox's cached-only-packages posture means new deps fail to restore. Package-adoption is a judgment call the caller owns. See the `clanker` skill for the full delegate/write/interpret/retry workflow.")]
    public static async Task<string> Build(
        IChatClient chat,
        IConfiguration config,
        string contractPath,
        [Description("Dev/test convenience only — absolute path to the target git repository to operate on. Normally unset: the server uses its own current working directory. Scheduled for removal before v2/shipping — the production flow is one Claude Code session per target repo.")]
        string? targetRepo = null)
    {
        if (!File.Exists(contractPath))
            return BuildResultJson.Serialize(RejectBuild("T-???", null, null, $"Contract file not found: {contractPath}"));

        var markdown = await File.ReadAllTextAsync(contractPath);
        var contract = ContractParser.Parse(markdown);

        var (resolvedTargetRepo, targetRepoError) = ResolveTargetRepo(targetRepo);
        if (resolvedTargetRepo is null)
            return BuildResultJson.Serialize(RejectBuild(contract.TaskId, null, null, targetRepoError!));

        var validation = ContractValidator.Validate(contract, resolvedTargetRepo);
        if (!validation.IsValid)
            return BuildResultJson.Serialize(RejectBuild(contract.TaskId, null, null, validation.RejectionReason!));

        string worktreePath;
        string branch;
        try
        {
            (worktreePath, branch) = Worktree.Create(resolvedTargetRepo, contract.TaskId);
        }
        catch (Exception ex)
        {
            return BuildResultJson.Serialize(RejectBuild(contract.TaskId, null, null,
                $"Failed to create git worktree for {contract.TaskId}: {ex.Message}"));
        }

        var traceDirectory = Worktree.TraceDir(resolvedTargetRepo, contract.TaskId);
        var providerName = config["ActiveProvider"];
        var providerSection = ResolveProviderSection(config, providerName);
        var modelName = providerSection?["Model"];
        var maxOutputTokens = ParseMaxOutputTokens(providerSection) ?? DefaultMaxOutputTokens;
        var sandbox = SandboxConfig.FromConfiguration(config);

        var result = await Executor.RunAsync(
            chat: chat,
            contract: contract,
            workingDirectory: worktreePath,
            branch: branch,
            providerName: providerName,
            modelName: modelName,
            maxToolCalls: 500,
            maxOutputTokens: maxOutputTokens,
            traceDirectory: traceDirectory,
            sandbox: sandbox,
            ct: CancellationToken.None);

        return BuildResultJson.Serialize(result);
    }

    // Default if the active provider doesn't pin a value. Matches the prior
    // hardcoded ceiling — see comment in Executor.RunAsync for why 16384.
    const int DefaultMaxOutputTokens = 16384;

    static IConfigurationSection? ResolveProviderSection(IConfiguration config, string? activeProvider)
    {
        if (string.IsNullOrEmpty(activeProvider)) return null;
        return config.GetSection("ChatProviders").GetChildren()
            .FirstOrDefault(p => string.Equals(p["Name"], activeProvider, StringComparison.OrdinalIgnoreCase));
    }

    static int? ParseMaxOutputTokens(IConfigurationSection? section)
    {
        var raw = section?["MaxOutputTokens"];
        return int.TryParse(raw, out var n) && n > 0 ? n : null;
    }

    // Normalizes and validates an optional caller-supplied targetRepo. Guards
    // against relative paths, non-existent directories, and non-git-repo
    // targets. The parameter itself is a dev/test convenience — see the
    // [Description] on Build's targetRepo parameter for the removal plan.
    static (string? Resolved, string? Error) ResolveTargetRepo(string? provided)
    {
        string candidate;
        try
        {
            candidate = Path.GetFullPath(provided ?? Directory.GetCurrentDirectory());
        }
        catch (Exception ex)
        {
            return (null, $"Could not resolve targetRepo path: {ex.Message}");
        }

        if (!Directory.Exists(candidate))
            return (null, $"targetRepo directory does not exist: {candidate}");

        // A git repo root has a `.git/` directory; a worktree has a `.git` file
        // pointing at the real gitdir. Accept both.
        var dotGit = Path.Combine(candidate, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit))
            return (null, $"targetRepo is not a git repository (no .git entry): {candidate}");

        return (candidate, null);
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
            TokensInputTotal: 0L,
            TokensOutputTotal: 0L,
            EstimatedCostUsd: 0m,
            FilesChanged: Array.Empty<FileChange>(),
            ScopeAdherence: new ScopeAdherence(InScope: true, OutOfScopePaths: Array.Empty<string>()),
            Tests: null,
            Acceptance: Array.Empty<AcceptanceCheck>(),
            SubAgentsSpawned: Array.Empty<SubAgentResult>(),
            Notes: "",
            BlockedQuestion: null,
            RejectionReason: reason,
            WorktreePath: worktreePath ?? "",
            Branch: branch ?? "",
            TracePath: "",
            TranscriptPath: "");
    }

    [McpServerTool, Description("List all contracts under `<target-repo>/contracts/*.md` with their task IDs and titles. Returns a JSON array of { task_id, title, file_path }. Use to inventory what's been written; call get_log(taskId) to see whether each has been run.")]
    public static string ListTasks(
        [Description("Dev/test convenience only — see build()'s targetRepo parameter.")]
        string? targetRepo = null)
    {
        var (resolved, error) = ResolveTargetRepo(targetRepo);
        if (resolved is null)
            return SerializeError(error!);

        var contractsDir = Path.Combine(resolved, "contracts");
        if (!Directory.Exists(contractsDir))
            return JsonSerializer.Serialize(Array.Empty<TaskSummary>(), TaskJsonOptions);

        var tasks = new List<TaskSummary>();
        foreach (var path in Directory.EnumerateFiles(contractsDir, "*.md", SearchOption.TopDirectoryOnly).OrderBy(p => p))
        {
            string markdown;
            try { markdown = File.ReadAllText(path); }
            catch { continue; }
            var contract = ContractParser.Parse(markdown);
            tasks.Add(new TaskSummary(contract.TaskId, contract.Title, path));
        }
        return JsonSerializer.Serialize(tasks, TaskJsonOptions);
    }

    [McpServerTool, Description("Return the raw markdown content of a contract identified by task ID (looks up `<target-repo>/contracts/T-<id>-*.md`). Use when a parent needs to re-read or revise the contract body.")]
    public static string GetContract(
        string taskId,
        [Description("Dev/test convenience only — see build()'s targetRepo parameter.")]
        string? targetRepo = null)
    {
        var (resolved, error) = ResolveTargetRepo(targetRepo);
        if (resolved is null)
            return SerializeError(error!);

        var path = FindContractByTaskId(resolved, taskId);
        if (path is null)
            return SerializeError($"No contract file matching task ID `{taskId}` found under `{Path.Combine(resolved, "contracts")}`.");

        return File.ReadAllText(path);
    }

    [McpServerTool, Description("Return the rendered markdown transcript of a contract's most recent run, if any. Reads `<parent>/<repo>.worktrees/<taskId>.trace/transcript.md`. Use when the proof-of-work notes aren't enough to diagnose what happened. For raw JSONL, read the sibling trace.jsonl directly.")]
    public static string GetLog(
        string taskId,
        [Description("Dev/test convenience only — see build()'s targetRepo parameter.")]
        string? targetRepo = null)
    {
        var (resolved, error) = ResolveTargetRepo(targetRepo);
        if (resolved is null)
            return SerializeError(error!);

        var traceDir = Worktree.TraceDir(resolved, taskId);
        var transcriptPath = Path.Combine(traceDir, "transcript.md");
        if (!File.Exists(transcriptPath))
            return SerializeError($"No transcript found for task `{taskId}` at `{transcriptPath}`. The task may not have been run yet, or the trace directory was cleaned up.");

        return File.ReadAllText(transcriptPath);
    }

    [McpServerTool, Description("Dry-run a contract: parse it and check structural validity + scope-file existence, without executing. Returns JSON { is_valid, rejection_reason, task_id, title, goal, scope, acceptance, non_goals }. Use before build() to catch contract errors without paying for a model turn.")]
    public static async Task<string> ValidateContract(
        string contractPath,
        [Description("Dev/test convenience only — see build()'s targetRepo parameter.")]
        string? targetRepo = null)
    {
        if (!File.Exists(contractPath))
            return SerializeError($"Contract file not found: {contractPath}");

        var markdown = await File.ReadAllTextAsync(contractPath);
        var contract = ContractParser.Parse(markdown);

        var (resolved, targetRepoError) = ResolveTargetRepo(targetRepo);
        if (resolved is null)
            return SerializeError(targetRepoError!);

        var validation = ContractValidator.Validate(contract, resolved);

        var result = new ValidationResult(
            IsValid: validation.IsValid,
            RejectionReason: validation.RejectionReason,
            TaskId: contract.TaskId,
            Title: contract.Title,
            Goal: contract.Goal,
            Scope: contract.Scope.Select(s => new ScopeSummary(s.Action.ToString().ToLowerInvariant(), s.Path)).ToArray(),
            Acceptance: contract.Acceptance,
            NonGoals: contract.NonGoals);

        return JsonSerializer.Serialize(result, TaskJsonOptions);
    }

    [McpServerTool, Description("Write or overwrite a contract markdown file at the given path. Parent directories are NOT auto-created — caller is responsible for the target location (convention: `<target-repo>/contracts/T-NNN-slug.md`).")]
    public static async Task<string> UpdateContract(string contractPath, string content)
    {
        var parent = Path.GetDirectoryName(contractPath);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            return SerializeError($"Parent directory does not exist: {parent}");

        var existedBefore = File.Exists(contractPath);
        await File.WriteAllTextAsync(contractPath, content);
        var bytes = new FileInfo(contractPath).Length;
        return JsonSerializer.Serialize(new UpdateResult(
            Path: contractPath,
            Action: existedBefore ? "overwrote" : "created",
            Bytes: bytes), TaskJsonOptions);
    }

    // --- helpers for the un-stubbed handlers ---

    static readonly JsonSerializerOptions TaskJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    static string SerializeError(string message)
        => JsonSerializer.Serialize(new { error = message }, TaskJsonOptions);

    // Finds `<targetRepo>/contracts/T-<taskId or matching-prefix>-*.md`. Accepts
    // taskId with or without the "T-" prefix, case-insensitively.
    static string? FindContractByTaskId(string targetRepo, string taskId)
    {
        var contractsDir = Path.Combine(targetRepo, "contracts");
        if (!Directory.Exists(contractsDir)) return null;

        var normalized = taskId.StartsWith("T-", StringComparison.OrdinalIgnoreCase) ? taskId : $"T-{taskId}";

        // Exact-id prefix: T-001.md or T-001-anything.md
        foreach (var path in Directory.EnumerateFiles(contractsDir, $"{normalized}*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(normalized + "-", StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }

    record TaskSummary(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("file_path")] string FilePath);

    record ValidationResult(
        [property: JsonPropertyName("is_valid")] bool IsValid,
        [property: JsonPropertyName("rejection_reason")] string? RejectionReason,
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("goal")] string Goal,
        [property: JsonPropertyName("scope")] IReadOnlyList<ScopeSummary> Scope,
        [property: JsonPropertyName("acceptance")] IReadOnlyList<string> Acceptance,
        [property: JsonPropertyName("non_goals")] IReadOnlyList<string> NonGoals);

    record ScopeSummary(
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("path")] string Path);

    record UpdateResult(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("bytes")] long Bytes);
}
