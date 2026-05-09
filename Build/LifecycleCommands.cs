using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Imp.Infrastructure;

namespace Imp.Build;

// Static surface methods invoked by the CLI dispatcher in Program.cs.
// Originally MCP-tool entry points; the [Description] attributes are
// retained as inline documentation and have no runtime effect now that
// the MCP layer has been removed.
//
// All methods operate on the current working directory as the target
// git repo. The previous `targetRepo` override parameter is gone — the
// CLI's contract is one process per invocation, scoped to wherever the
// user ran it from.

public static class LifecycleCommands
{
    [Description("Smoke test: asks the configured provider to reply with a short greeting. Use to verify provider wiring.")]
    public static async Task<string> Ping(IChatClient chat, string? message = null)
    {
        var prompt = message ?? "Reply with exactly the word 'pong' and nothing else.";
        var response = await chat.GetResponseAsync(prompt);
        return response.Text;
    }

    [Description("Executes a contract file through a cheap, slow coding executor (default: Azure GPT-5.1-codex-mini) in a fresh git worktree, and returns a structured proof-of-work JSON. Long-running: minutes to tens of minutes. Persists the proof-of-work to <trace-dir>/proof-of-work.json so `imp review` can read it later.")]
    public static async Task<string> Build(
        IChatClient chat,
        IConfiguration config,
        string contractPath)
    {
        ImpLog.Info($"build: start contractPath={contractPath} cwd={Directory.GetCurrentDirectory()}");

        if (!File.Exists(contractPath))
            return BuildResultJson.Serialize(RejectBuild("T-???", null, null, $"Contract file not found: {contractPath}"));

        var markdown = await File.ReadAllTextAsync(contractPath);
        var contract = ContractParser.Parse(markdown);
        ImpLog.Info($"build: parsed taskId={contract.TaskId} title={contract.Title}");

        var (resolvedTargetRepo, targetRepoError) = ResolveTargetRepo();
        if (resolvedTargetRepo is null)
            return BuildResultJson.Serialize(RejectBuild(contract.TaskId, null, null, targetRepoError!));
        ImpLog.Info($"build: targetRepo resolved to {resolvedTargetRepo}");

        var validation = ContractValidator.Validate(contract, resolvedTargetRepo);
        if (!validation.IsValid)
            return BuildResultJson.Serialize(RejectBuild(contract.TaskId, null, null, validation.RejectionReason!));
        ImpLog.Info($"build: contract validated taskId={contract.TaskId}");

        string worktreePath;
        string branch;
        try
        {
            ImpLog.Info($"build: creating worktree for {contract.TaskId}");
            (worktreePath, branch) = Worktree.Create(resolvedTargetRepo, contract.TaskId);
            ImpLog.Info($"build: worktree created path={worktreePath} branch={branch}");
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

        ImpLog.Info($"build: executor starting taskId={contract.TaskId} provider={providerName} model={modelName} sandbox={sandbox.Mode}");
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
        ImpLog.Info($"build: executor completed taskId={contract.TaskId} terminal={result.TerminalState} toolCalls={result.ToolCallCount}");

        // Auto-commit on evaluator sign-off. Closeout already demoted to
        // Failure if any verdict failed (Executor.RunCloseoutAsync), so
        // Success here means the evaluator approved the diff. Failures
        // bubble into imp.log but don't tank the proof-of-work — the parent
        // can still inspect/commit manually if needed.
        if (result.TerminalState == TerminalState.Success && !string.IsNullOrEmpty(result.WorktreePath))
        {
            try
            {
                var message = $"{result.TaskId}: {contract.Title}";
                var committed = Worktree.CommitAll(result.WorktreePath, message);
                ImpLog.Info(committed
                    ? $"build: auto-committed taskId={result.TaskId}"
                    : $"build: auto-commit skipped (worktree clean) taskId={result.TaskId}");
            }
            catch (Exception ex)
            {
                ImpLog.Warn($"build: auto-commit failed taskId={result.TaskId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var json = BuildResultJson.Serialize(result);

        // Persist proof-of-work next to transcript.md so `imp review`
        // can read structured fields without re-executing or scraping
        // trace.jsonl. Best-effort — if the trace dir vanished we still
        // return the JSON to the caller.
        try
        {
            Directory.CreateDirectory(traceDirectory);
            var proofPath = Path.Combine(traceDirectory, "proof-of-work.json");
            await File.WriteAllTextAsync(proofPath, json);
            ImpLog.Info($"build: proof-of-work persisted at {proofPath}");
        }
        catch (Exception ex)
        {
            ImpLog.Warn($"build: failed to persist proof-of-work.json: {ex.GetType().Name}: {ex.Message}");
        }

        return json;
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

    // Resolves and validates the current working directory as a git repo.
    // The CLI is one-process-per-invocation, so cwd is the contract.
    static (string? Resolved, string? Error) ResolveTargetRepo()
    {
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Directory.GetCurrentDirectory());
        }
        catch (Exception ex)
        {
            return (null, $"Could not resolve cwd: {ex.Message}");
        }

        if (!Directory.Exists(candidate))
            return (null, $"cwd does not exist: {candidate}");

        // A git repo root has a `.git/` directory; a worktree has a `.git` file
        // pointing at the real gitdir. Accept both.
        var dotGit = Path.Combine(candidate, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit))
            return (null, $"cwd is not a git repository (no .git entry): {candidate}");

        return (candidate, null);
    }

    static BuildResult RejectBuild(string taskId, string? worktreePath, string? branch, string reason)
    {
        ImpLog.Warn($"build: rejected taskId={taskId} reason={reason}");
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

    [Description("List all contracts under `<cwd>/contracts/*.md` with their task IDs and titles. Returns a JSON array of { task_id, title, file_path }.")]
    public static string ListTasks()
    {
        var (resolved, error) = ResolveTargetRepo();
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

    [Description("Return the raw markdown content of a contract identified by task ID (looks up `<cwd>/contracts/T-<id>-*.md`).")]
    public static string GetContract(string taskId)
    {
        var (resolved, error) = ResolveTargetRepo();
        if (resolved is null)
            return SerializeError(error!);

        var path = FindContractByTaskId(resolved, taskId);
        if (path is null)
            return SerializeError($"No contract file matching task ID `{taskId}` found under `{Path.Combine(resolved, "contracts")}`.");

        return File.ReadAllText(path);
    }

    [Description("Return the rendered markdown transcript of a contract's most recent run, if any. Reads `<parent>/<repo>.worktrees/<taskId>.trace/transcript.md`.")]
    public static string GetLog(string taskId)
    {
        var (resolved, error) = ResolveTargetRepo();
        if (resolved is null)
            return SerializeError(error!);

        var traceDir = Worktree.TraceDir(resolved, taskId);
        var transcriptPath = Path.Combine(traceDir, "transcript.md");
        if (!File.Exists(transcriptPath))
            return SerializeError($"No transcript found for task `{taskId}` at `{transcriptPath}`. The task may not have been run yet, or the trace directory was cleaned up.");

        return File.ReadAllText(transcriptPath);
    }

    [Description("Dry-run a contract: parse it and check structural validity + scope-file existence, without executing.")]
    public static async Task<string> ValidateContract(string contractPath)
    {
        if (!File.Exists(contractPath))
            return SerializeError($"Contract file not found: {contractPath}");

        var markdown = await File.ReadAllTextAsync(contractPath);
        var contract = ContractParser.Parse(markdown);

        var (resolved, targetRepoError) = ResolveTargetRepo();
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

    [Description("Bundled post-build view: proof-of-work summary + git diff of contract/<task-id> against HEAD. Keeps the parent out of the worktree.")]
    public static string Review(string taskId)
    {
        var (resolved, error) = ResolveTargetRepo();
        if (resolved is null)
            return $"# Review: {taskId}\n\n**Error:** {error}\n";

        var traceDir = Worktree.TraceDir(resolved, taskId);
        var proofPath = Path.Combine(traceDir, "proof-of-work.json");
        var transcriptPath = Path.Combine(traceDir, "transcript.md");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Review: {taskId}");
        sb.AppendLine();

        if (!File.Exists(proofPath))
        {
            sb.AppendLine($"**No proof-of-work found at `{proofPath}`.** The build may not have completed, or the trace directory was cleaned up.");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Proof of work");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(File.ReadAllText(proofPath).TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine();
        }

        var branch = $"contract/{NormalizeTaskId(taskId)}";
        sb.AppendLine($"## Diff: `git diff HEAD...{branch}`");
        sb.AppendLine();
        var (diffOk, diffOutput) = RunGitCapture(resolved, "diff", $"HEAD...{branch}");
        if (!diffOk)
        {
            sb.AppendLine($"**git diff failed:** {diffOutput.Trim()}");
        }
        else if (string.IsNullOrWhiteSpace(diffOutput))
        {
            sb.AppendLine("*(no changes)*");
        }
        else
        {
            sb.AppendLine("```diff");
            sb.AppendLine(diffOutput.TrimEnd());
            sb.AppendLine("```");
        }
        sb.AppendLine();

        if (File.Exists(transcriptPath))
        {
            sb.AppendLine($"For full executor turn-by-turn detail: `imp log {taskId}` (or open `{transcriptPath}`).");
        }

        return sb.ToString();
    }

    static string NormalizeTaskId(string taskId)
        => taskId.StartsWith("T-", StringComparison.OrdinalIgnoreCase) ? taskId : $"T-{taskId}";

    // Lightweight git invocation that returns (success, combined-output).
    // Distinct from Worktree.RunGit, which throws — review must not throw on
    // a missing branch; it should explain the failure to the caller.
    static (bool Ok, string Output) RunGitCapture(string cwd, params string[] args)
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
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
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (false, "git timed out after 30s");
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
                return (false, string.IsNullOrWhiteSpace(stderr) ? $"exit {proc.ExitCode}" : stderr);
            return (true, stdout);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // --- helpers ---

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

        var normalized = NormalizeTaskId(taskId);

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
}
