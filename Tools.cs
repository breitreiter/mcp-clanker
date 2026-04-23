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
    public int ToolCallCount { get; set; }
    public Dictionary<string, FileAction> FilesTouched { get; } = new();

    // Set by any tool that detects a safety-gate violation (e.g. bash command
    // matching a danger pattern). Executor checks this after each tool-call
    // batch and terminates the run as blocked if present. Only the first
    // breach wins — a safety breach is terminal, not advisory.
    public SafetyBreach? SafetyBreach { get; private set; }

    public void FlagSafetyBreach(SafetyBreach breach)
    {
        SafetyBreach ??= breach;
    }

    public void RecordWrite(string relativePath, bool existedBefore)
    {
        if (!FilesTouched.ContainsKey(relativePath))
            FilesTouched[relativePath] = existedBefore ? FileAction.Modified : FileAction.Created;
    }
}

public record SafetyBreach(BlockedCategory Category, string Summary, string OffendingInput);

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
