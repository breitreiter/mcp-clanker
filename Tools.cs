using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace Imp;

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

    // Populated by the `finish_work` tool during the self-check phase.
    // Null if the phase never ran (non-success terminal) or if the model
    // skipped the finish_work call even after the nudge.
    public List<AcceptanceReport>? AcceptanceReports { get; set; }

    // Bullets parsed from the contract's optional `**Allowed network:**`
    // section. Non-empty means the contract author has consciously declared
    // that this run needs network access — the soft egress gate (regex over
    // bash commands) stands aside in that case. The list contents are
    // documentary intent for human readers; the gate doesn't try to validate
    // commands against specific hostnames. Docker `--network=none` (when the
    // sandbox is enabled) is the strong layer and is unaffected by this flag.
    public IReadOnlyList<string> AllowedNetwork { get; set; } = Array.Empty<string>();

    // Populated by the `finish_work` tool during the closeout phase. Closeout
    // is an independent review (fresh context, diff-only input, read-only
    // tools); if it runs and reports, its verdicts OVERRIDE AcceptanceReports
    // in the returned POW — see Executor.RunAsync.
    public List<AcceptanceReport>? CloseoutReports { get; set; }

    // Free-text summary the closeout reviewer optionally produces alongside
    // its per-bullet verdicts. Landing in SubAgentResult.Notes.
    public string? CloseoutNotes { get; set; }

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

// Shape accepted by the `finish_work` tool during the self-check phase.
// Citation is the honesty-enforcement knob — the model must point at
// something concrete (file:line, tool-call summary, diff reference) so
// its verdict isn't pure assertion.
public record AcceptanceReport(string Item, string Status, string Citation);

public static class Tools
{
    const int MaxToolOutputBytes = 8 * 1024;
    const int BashTimeoutSeconds = 120;

    public static IList<AITool> Create(string workingDirectory, ExecutorState state, SandboxConfig sandbox)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                (
                    [Description("The bash command to execute. Runs in the contract's worktree.")] string command,
                    [Description("Short rationale for the command, 1 sentence. Optional.")] string? description = null)
                => RunBash(command, workingDirectory, state, sandbox),
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
                    [Description("A complete patch in the codex sentinel format. Must start with '*** Begin Patch' and end with '*** End Patch'.")] string input)
                => ApplyPatchInvoke(input, workingDirectory, state),
                name: "apply_patch",
                description: """
                    Apply a multi-file patch. **Preferred for GPT-family models**, which are
                    trained on this exact format; other model families may prefer write_file.

                    The `input` parameter contains a complete patch in this format:

                        *** Begin Patch
                        *** Update File: path/to/file
                        @@ class Foo
                        @@   def bar(self):
                         context line (unchanged)
                        -line to remove
                        +line to add
                         context line (unchanged)
                        *** Add File: path/to/new
                        +every line of the new file
                        *** Delete File: path/to/old
                        *** End Patch

                    Rules:
                      - Paths are relative to the contract's worktree.
                      - Each change line starts with exactly one sigil: ' ' (context), '-' (remove), '+' (add). The character immediately after the sigil is content. No line numbers.
                      - Include ~3 lines of unchanged context before and after each change so the chunk anchors uniquely — enough to disambiguate, not so much as to be wasteful.
                      - `@@ header` lines anchor a chunk's location; stack multiple headers to descend into nested scopes (e.g. `@@ class Foo` then `@@   def bar`).
                      - Within one Update File, chunks are located in document order via a forward-only cursor — duplicate code must be disambiguated with @@ headers.
                      - `*** End of File` after a chunk means "this edit is at EOF".
                      - Rename via `*** Move to: new/path` placed immediately after `*** Update File:`.

                    For large changes:
                      - Split the edit into many small `@@`-anchored chunks rather than one giant chunk — small chunks locate reliably and are cheaper to emit.
                      - To replace an entire file, use `*** Delete File:` followed by `*** Add File:` instead of diffing the whole body.
                      - Never include unchanged regions of the file outside the ~3-line context window of an actual change.

                    On success returns a per-file summary (Add / Update / Delete / UpdateAndMove with line counts). On parse or apply failure returns an error with the patch NOT partially applied.
                    """),

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

    // Read-only subset used by the closeout reviewer. Same implementations as
    // the main-loop tools, minus anything that mutates state or the filesystem.
    // No ExecutorState plumbing — closeout's only state-write is finish_work,
    // which is constructed inline in Executor.RunCloseoutAsync against
    // state.CloseoutReports.
    public static IList<AITool> CreateReadOnly(string workingDirectory)
    {
        return new List<AITool>
        {
            AIFunctionFactory.Create(
                (
                    [Description("Path relative to the working directory.")] string path,
                    [Description("Optional 1-based line to start from. Defaults to the whole file.")] int? offset = null,
                    [Description("Optional max number of lines to return.")] int? limit = null)
                => ReadFile(path, offset, limit, workingDirectory),
                name: "read_file",
                description: "Read the contents of a text file relative to the working directory."),

            AIFunctionFactory.Create(
                (
                    [Description("Regular expression to search for.")] string pattern,
                    [Description("Directory or file to search, relative to the working directory. Empty or omitted = the whole working directory.")] string? path = null,
                    [Description("Filename glob filter like `*.cs` or `*Test*.cs`. Applied to filename only.")] string? file_pattern = null,
                    [Description("If true, case-insensitive search. Default false.")] bool? case_insensitive = null,
                    [Description("Max results to return. Default 100.")] int? max_results = null,
                    [Description("`content` (default) returns matching lines as `file:line: content`. `files_with_matches` returns only matching file paths.")] string? output_mode = null)
                => GrepTool.Grep(pattern, path, file_pattern, case_insensitive, max_results, output_mode, workingDirectory),
                name: "grep",
                description: "Search file contents with a regex. Skips binary files and common non-source directories."),

            AIFunctionFactory.Create(
                (
                    [Description("Directory path relative to the working directory. Empty or omitted = the working directory itself.")] string? path = null)
                => ListDir(path, workingDirectory),
                name: "list_dir",
                description: "List the contents of a directory."),
        };
    }

    // --- bash ---

    static async Task<string> RunBash(string command, string cwd, ExecutorState state, SandboxConfig sandbox)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "ERROR: empty command.";

        var danger = CommandClassifier.Classify(command, sandbox.Mode);
        if (danger.IsDangerous)
        {
            state.FlagSafetyBreach(new SafetyBreach(
                Category: BlockedCategory.Abandon,
                Summary: $"Bash command blocked by safety gate: {danger.Reason}.",
                OffendingInput: command));
            return $"ERROR: command blocked by safety gate: {danger.Reason}. This run will terminate.";
        }

        // Soft egress gate is Host-mode-only. Under Docker, `--network=none`
        // is the structural wall — the regex check is redundant and the
        // **Allowed network:** annotation in contracts becomes documentary
        // intent rather than a runtime knob.
        if (sandbox.Mode == SandboxMode.Host && state.AllowedNetwork.Count == 0)
        {
            var egress = NetworkEgressChecker.Check(command);
            if (egress.IsBlocked)
            {
                state.FlagSafetyBreach(new SafetyBreach(
                    Category: BlockedCategory.RescopeOrCapability,
                    Summary: $"Bash command blocked by network-egress gate: {egress.Reason}.",
                    OffendingInput: command));
                return $"ERROR: command blocked by network-egress gate: {egress.Reason}. This run will terminate.";
            }
        }

        using var proc = BuildBashProcess(command, cwd, sandbox);

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

    // Build the Process that will run the bash command. In Host mode that's
    // the resolved host bash (`/bin/bash` on Unix, Git Bash on Windows — see
    // ShellResolver) invoked with `-c <cmd>`. In Docker mode it's `docker run --rm` with the
    // worktree bind-mounted at /work, a shared nuget volume at /root/.nuget/
    // packages, and --network=<configured, default "none">. Kill-on-timeout
    // semantics work in both modes: killing the client process triggers
    // `docker run --rm` to tear down the container.
    static Process BuildBashProcess(string command, string cwd, SandboxConfig sandbox)
    {
        if (sandbox.Mode == SandboxMode.Host)
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ShellResolver.Resolve(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = cwd,
                },
            };
            proc.StartInfo.ArgumentList.Add("-c");
            proc.StartInfo.ArgumentList.Add(command);
            return proc;
        }

        // Docker mode. Worktree bind-mount is rw (contract needs to write
        // build artifacts back to /work, which round-trip to the host via
        // the bind). Nuget cache is a named volume, shared across contract
        // runs, so common packages are downloaded at most once ever.
        // --network defaults to "none" so a compromised or confused run
        // can't exfiltrate; contracts that legitimately need a new
        // package fail the `dotnet restore` and return cleanly — the
        // parent is expected to handle dep decisions, not imp.
        var docker = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        var args = docker.StartInfo.ArgumentList;
        args.Add("run");
        args.Add("--rm");
        args.Add("--network"); args.Add(sandbox.Network);
        args.Add("--memory"); args.Add(sandbox.MemoryLimit);
        args.Add("--cpus"); args.Add(sandbox.CpuLimit);
        args.Add("--pids-limit"); args.Add(sandbox.PidsLimit.ToString());

        // On Linux, run as the host user so files written into /work
        // (the bind-mounted worktree) are owned by the host user, not root.
        // Otherwise `git worktree remove` and ordinary `rm` from the host
        // fail on container-created files. Docker Desktop on Windows handles
        // bind-mount ownership through its own translation layer — no flag
        // needed there. macOS untested; treat as Windows-like for now.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            args.Add("--user"); args.Add($"{Posix.GetUid()}:{Posix.GetGid()}");
        }

        // HOME=/tmp keeps dotnet's first-run state (telemetry, NuGet config)
        // on a world-writable path inside the container. /tmp is wiped on --rm
        // exit; the cost is dotnet's ~50ms first-run noise per contract,
        // accepted to avoid a second persistent volume just for SDK state.
        args.Add("--env"); args.Add("HOME=/tmp");
        args.Add("-v"); args.Add($"{Path.GetFullPath(cwd)}:/work");
        args.Add("-v"); args.Add($"{sandbox.NugetVolume}:/tmp/.nuget/packages");
        args.Add("-w"); args.Add("/work");
        args.Add(sandbox.Image);
        args.Add("/bin/bash");
        args.Add("-c");
        args.Add(command);
        return docker;
    }

    // libc bindings for resolving the host user/group at runtime, so the
    // docker `--user` flag can match. Linux-only; the call site gates on
    // RuntimeInformation.IsOSPlatform(OSPlatform.Linux).
    static class Posix
    {
        [DllImport("libc")] static extern uint getuid();
        [DllImport("libc")] static extern uint getgid();
        public static uint GetUid() => getuid();
        public static uint GetGid() => getgid();
    }

    // --- apply_patch ---

    static string ApplyPatchInvoke(string input, string cwd, ExecutorState state)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "ERROR: apply_patch input is empty.";

        List<FileOp> ops;
        try
        {
            ops = PatchParser.Parse(input);
        }
        catch (PatchParseException ex)
        {
            return $"ERROR: patch parse failed: {ex.Message}";
        }

        if (ops.Count == 0)
            return "ERROR: patch contained no file operations.";

        PatchPreview preview;
        try
        {
            preview = PatchApplier.BuildPreview(ops, cwd);
        }
        catch (PatchApplyException ex)
        {
            return $"ERROR: patch validation failed: {ex.Message}";
        }

        try
        {
            PatchApplier.Apply(preview, ops, cwd);
        }
        catch (Exception ex)
        {
            return $"ERROR: patch application failed mid-flight (files may be partially modified): {ex.GetType().Name}: {ex.Message}";
        }

        var summaryLines = new List<string>();
        foreach (var fp in preview.Files)
        {
            var relFinal = Path.GetRelativePath(cwd, fp.FinalPath);
            switch (fp.Kind)
            {
                case FileOpKind.Add:
                    state.RecordWrite(relFinal, existedBefore: false);
                    summaryLines.Add($"[Add]    {relFinal} ({fp.NewLineCount} lines)");
                    break;
                case FileOpKind.Delete:
                    state.FilesTouched[fp.OriginalPath] = FileAction.Deleted;
                    summaryLines.Add($"[Delete] {fp.OriginalPath} ({fp.OldLineCount} lines removed)");
                    break;
                case FileOpKind.Update:
                    state.RecordWrite(fp.OriginalPath, existedBefore: true);
                    summaryLines.Add($"[Update] {fp.OriginalPath} (−{fp.OldLineCount} / +{fp.NewLineCount} lines)");
                    break;
                case FileOpKind.UpdateAndMove:
                    state.FilesTouched[fp.OriginalPath] = FileAction.Deleted;
                    state.RecordWrite(relFinal, existedBefore: false);
                    summaryLines.Add($"[Move]   {fp.OriginalPath} → {relFinal} (−{fp.OldLineCount} / +{fp.NewLineCount} lines)");
                    break;
            }
        }

        return "patch applied.\n" + string.Join("\n", summaryLines);
    }

    // --- list_dir ---

    // Must match GrepTool's SkipDirectories. Intentional duplication for now —
    // consolidate into a shared constant if a third file tool needs the list.
    static readonly HashSet<string> ListDirSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", "__pycache__",
        ".venv", "venv", ".idea", "dist", "build", ".next", ".nuget",
    };

    internal static string ListDir(string? path, string cwd)
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

    internal static string ReadFile(string path, int? offset, int? limit, string cwd)
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
