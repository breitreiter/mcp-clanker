using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;

namespace McpClanker;

// Minimal tool set for v1: bash, read_file, write_file.
// Safety gates (CommandClassifier, network-egress, apply_patch) come in a
// follow-up pass after the first end-to-end works.
//
// Each tool captures a shared ExecutorState via closure so it can record
// files touched and tool-call bookkeeping without threading extra
// parameters through the call chain.

public class ExecutorState
{
    // Cap on how far back the doom-loop detector can look. Larger than any
    // current detection threshold (M=5) so the detector always has enough
    // history to work with, while keeping the buffer tiny.
    const int RecentCallsCapacity = 10;

    public int ToolCallCount { get; set; }
    public Dictionary<string, FileAction> FilesTouched { get; } = new();
    public TodoManager Todos { get; } = new();

    readonly List<ToolCallRecord> _recentCalls = new();
    public IReadOnlyList<ToolCallRecord> RecentCalls => _recentCalls;

    // Set by any tool or detector that detects a safety-gate violation
    // (bash command matching a danger pattern, network-egress trigger,
    // doom-loop). Executor checks this after each tool-call batch and
    // terminates the run as blocked if present. Only the first breach
    // wins — a safety breach is terminal, not advisory.
    public SafetyBreach? SafetyBreach { get; private set; }

    public void FlagSafetyBreach(SafetyBreach breach)
    {
        SafetyBreach ??= breach;
    }

    public void RecordToolCall(ToolCallRecord record)
    {
        _recentCalls.Add(record);
        if (_recentCalls.Count > RecentCallsCapacity)
            _recentCalls.RemoveAt(0);
    }

    public void RecordWrite(string relativePath, bool existedBefore)
    {
        if (!FilesTouched.ContainsKey(relativePath))
            FilesTouched[relativePath] = existedBefore ? FileAction.Modified : FileAction.Created;
    }
}

public record SafetyBreach(BlockedCategory Category, string Summary, string OffendingInput);

public record ToolCallRecord(string Name, string ArgsSignature, bool Success);

public static class Tools
{
    const int MaxToolOutputBytes = 8 * 1024;
    const int BashTimeoutSeconds = 120;

    public static IList<AITool> Create(string workingDirectory, ExecutorState state)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                (
                    [Description("The bash command to execute. Runs in the contract's worktree.")] string command,
                    [Description("Short rationale for the command, 1 sentence. Optional.")] string? description = null)
                => RunBash(command, workingDirectory, state),
                name: "bash",
                description: "Execute a bash command in the contract's working directory. Stdout + stderr are captured and returned. Large output is truncated."),

            AIFunctionFactory.Create(
                (
                    [Description("Path relative to the working directory.")] string path,
                    [Description("Optional 1-based line to start from. Defaults to the whole file.")] int? offset = null,
                    [Description("Optional max number of lines to return.")] int? limit = null)
                => ReadFile(path, offset, limit, workingDirectory),
                name: "read_file",
                description: "Read the contents of a text file relative to the working directory. Supports pagination via offset/limit."),

            AIFunctionFactory.Create(
                (
                    [Description("Path relative to the working directory. Parent directories are created as needed.")] string path,
                    [Description("Full file contents to write. Overwrites any existing file.")] string content)
                => WriteFile(path, content, workingDirectory, state),
                name: "write_file",
                description: "Create or overwrite a file at the given path with the supplied contents."),

            AIFunctionFactory.Create(
                (
                    [Description("Regular expression to search for.")] string pattern,
                    [Description("Directory or file to search, relative to the working directory. Empty or omitted = the whole working directory.")] string? path = null,
                    [Description("Filename glob filter like `*.cs` or `*Test*.cs`. Applied to filename only; path-qualified globs aren't supported — narrow with `path=` instead.")] string? file_pattern = null,
                    [Description("If true, case-insensitive search. Default false.")] bool? case_insensitive = null,
                    [Description("Max results to return. Default 100.")] int? max_results = null,
                    [Description("`content` (default) returns matching lines as `file:line: content`. `files_with_matches` returns only matching file paths.")] string? output_mode = null)
                => GrepTool.Grep(pattern, path, file_pattern, case_insensitive, max_results, output_mode, workingDirectory),
                name: "grep",
                description: "Search file contents with a regex. Automatically skips binary files and common non-source directories (.git, node_modules, bin, obj, .vs, __pycache__, .venv, venv, .idea, dist, build, .next, .nuget). Returns `file:line: content` lines by default; set output_mode=files_with_matches for file paths only."),

            AIFunctionFactory.Create(
                (
                    [Description("Directory path relative to the working directory. Empty or omitted = the working directory itself.")] string? path = null)
                => ListDir(path, workingDirectory),
                name: "list_dir",
                description: "List the contents of a directory. Returns `[dir] name` and `[file] name` entries, directories first, both alphabetically sorted. Skips the same non-source directories as grep."),

            AIFunctionFactory.Create(
                () => state.Todos.Render(),
                name: "todo_read",
                description: "Read the current session's todo checklist. Returns lines in `- [status] content` format, or `(no todos)` if empty. Use to check what's pending or in progress without guessing."),

            AIFunctionFactory.Create(
                (
                    [Description("Array of { content, status } changes. `content` is the unique key: unknown adds, known updates, `cancelled` removes. `status` is one of: pending, in_progress, completed, cancelled.")] List<TodoChange> changes)
                => WriteTodos(changes, state),
                name: "todo_write",
                description: """
                    Create or update the session's task checklist. Use this to plan multi-step
                    work and track progress as you go. Strongly recommended when a contract has
                    3+ distinct steps — write the checklist FIRST, then execute against it.

                    How it works:
                      - Each change has `content` (unique key, the task description) and `status`.
                      - Statuses: pending | in_progress | completed | cancelled.
                      - Unknown content → added as new task.
                      - Known content → status updated.
                      - cancelled → removed.
                      - Only send the items that CHANGED; items you don't mention stay as-is.

                    Rules:
                      - Mark a task `in_progress` BEFORE you start it, not after.
                      - Mark `completed` IMMEDIATELY on finish — don't batch.
                      - Only ONE task in_progress at a time.
                      - Don't mark completed if tests fail, work is partial, or you're blocked.
                      - Blocked? Keep in_progress and add a new task describing the blocker.
                    """),
        };

        return tools;
    }

    // --- bash ---

    static async Task<string> RunBash(string command, string cwd, ExecutorState state)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "ERROR: empty command.";

        var danger = CommandClassifier.Classify(command);
        if (danger.IsDangerous)
        {
            state.FlagSafetyBreach(new SafetyBreach(
                Category: BlockedCategory.Abandon,
                Summary: $"Bash command blocked by safety gate: {danger.Reason}.",
                OffendingInput: command));
            return $"ERROR: command blocked by safety gate: {danger.Reason}. This run will terminate.";
        }

        var egress = NetworkEgressChecker.Check(command);
        if (egress.IsBlocked)
        {
            state.FlagSafetyBreach(new SafetyBreach(
                Category: BlockedCategory.RescopeOrCapability,
                Summary: $"Bash command blocked by network-egress gate: {egress.Reason}.",
                OffendingInput: command));
            return $"ERROR: command blocked by network-egress gate: {egress.Reason}. This run will terminate.";
        }

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = cwd,
            },
        };
        proc.StartInfo.ArgumentList.Add("-c");
        proc.StartInfo.ArgumentList.Add(command);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(BashTimeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return $"ERROR: bash command timed out after {BashTimeoutSeconds}s.\n\n--- stdout ---\n{Truncate(stdout.ToString())}\n--- stderr ---\n{Truncate(stderr.ToString())}";
        }

        var output = new StringBuilder();
        output.AppendLine($"exit_code: {proc.ExitCode}");
        if (stdout.Length > 0)
        {
            output.AppendLine("--- stdout ---");
            output.Append(Truncate(stdout.ToString()));
        }
        if (stderr.Length > 0)
        {
            output.AppendLine("--- stderr ---");
            output.Append(Truncate(stderr.ToString()));
        }
        return output.ToString();
    }

    // --- list_dir ---

    // Must match GrepTool's SkipDirectories. Intentional duplication for now —
    // consolidate into a shared constant if a third file tool needs the list.
    static readonly HashSet<string> ListDirSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", "__pycache__",
        ".venv", "venv", ".idea", "dist", "build", ".next", ".nuget",
    };

    static string ListDir(string? path, string cwd)
    {
        var resolved = ResolveInsideCwd(path ?? "", cwd);
        if (resolved is null)
            return $"ERROR: path '{path}' resolves outside the working directory.";
        if (!Directory.Exists(resolved))
            return $"ERROR: directory '{path}' does not exist.";

        var entries = new List<string>();

        foreach (var dir in Directory.GetDirectories(resolved).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (!ListDirSkip.Contains(name))
                entries.Add($"[dir]  {name}");
        }

        foreach (var file in Directory.GetFiles(resolved).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            entries.Add($"[file] {Path.GetFileName(file)}");

        return entries.Count > 0 ? string.Join("\n", entries) : "(empty directory)";
    }

    // --- todo_write ---

    static string WriteTodos(List<TodoChange> changes, ExecutorState state)
    {
        if (changes is null || changes.Count == 0)
            return "No changes submitted.\n\nCurrent list:\n" + state.Todos.Render();

        var applied = state.Todos.ApplyChanges(changes);
        var current = state.Todos.Render();
        return $"Changes applied:\n{string.Join("\n", applied)}\n\nCurrent list:\n{current}";
    }

    // --- read_file ---

    static string ReadFile(string path, int? offset, int? limit, string cwd)
    {
        var resolved = ResolveInsideCwd(path, cwd);
        if (resolved is null)
            return $"ERROR: path '{path}' resolves outside the working directory.";
        if (!File.Exists(resolved))
            return $"ERROR: file '{path}' does not exist.";

        try
        {
            var lines = File.ReadAllLines(resolved);
            var start = Math.Max(0, (offset ?? 1) - 1);
            var take = limit ?? lines.Length;
            var slice = lines.Skip(start).Take(take).ToArray();
            var body = string.Join('\n', slice);
            return Truncate(body);
        }
        catch (Exception ex)
        {
            return $"ERROR: reading '{path}' failed: {ex.Message}";
        }
    }

    // --- write_file ---

    static string WriteFile(string path, string content, string cwd, ExecutorState state)
    {
        var resolved = ResolveInsideCwd(path, cwd);
        if (resolved is null)
            return $"ERROR: path '{path}' resolves outside the working directory.";

        var existedBefore = File.Exists(resolved);
        try
        {
            var parent = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(resolved, content);
            state.RecordWrite(path, existedBefore);
            return existedBefore
                ? $"overwrote {path} ({content.Length} bytes)"
                : $"created {path} ({content.Length} bytes)";
        }
        catch (Exception ex)
        {
            return $"ERROR: writing '{path}' failed: {ex.Message}";
        }
    }

    // --- helpers ---

    // Returns the absolute resolved path iff it falls inside cwd; null otherwise.
    // Protects against path-traversal via "..".
    static string? ResolveInsideCwd(string path, string cwd)
    {
        var absCwd = Path.GetFullPath(cwd);
        var candidate = Path.IsPathRooted(path) ? path : Path.Combine(absCwd, path);
        var resolved = Path.GetFullPath(candidate);
        var normalizedCwd = absCwd.EndsWith(Path.DirectorySeparatorChar) ? absCwd : absCwd + Path.DirectorySeparatorChar;
        if (resolved == absCwd || resolved.StartsWith(normalizedCwd, StringComparison.Ordinal))
            return resolved;
        return null;
    }

    static string Truncate(string s)
    {
        if (s.Length <= MaxToolOutputBytes) return s;
        var half = MaxToolOutputBytes / 2;
        return s[..half] + $"\n... [truncated {s.Length - MaxToolOutputBytes} bytes] ...\n" + s[^half..];
    }
}
