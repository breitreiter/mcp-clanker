using System.Diagnostics;

namespace McpClanker;

// Creates a fresh git worktree for a contract run. Path convention:
//   <target-repo>/../<target-repo-name>.worktrees/<task-id>
// Branch convention: contract/<task-id>
//
// Worktrees are NOT automatically cleaned up. On success/block/fail the
// parent (Claude Code) decides whether to merge, inspect, or remove.

public static class Worktree
{
    // Resolves <parent>/<repo>.worktrees/<task-id>.trace/ — sibling-of-worktree,
    // so all per-contract artefacts cluster under one parent dir.
    public static string TraceDir(string targetRepo, string taskId)
    {
        var absTarget = System.IO.Path.GetFullPath(targetRepo);
        var repoName = System.IO.Path.GetFileName(absTarget.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        var parent = System.IO.Path.GetDirectoryName(absTarget)
            ?? throw new InvalidOperationException($"Target repo '{absTarget}' has no parent directory.");
        return System.IO.Path.Combine(parent, $"{repoName}.worktrees", $"{taskId}.trace");
    }

    public static (string Path, string Branch) Create(string targetRepo, string taskId)
    {
        var absTarget = System.IO.Path.GetFullPath(targetRepo);
        var repoName = System.IO.Path.GetFileName(absTarget.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        var parent = System.IO.Path.GetDirectoryName(absTarget)
            ?? throw new InvalidOperationException($"Target repo '{absTarget}' has no parent directory.");
        var worktreeRoot = System.IO.Path.Combine(parent, $"{repoName}.worktrees");
        var worktreePath = System.IO.Path.Combine(worktreeRoot, taskId);
        var branch = $"contract/{taskId}";

        if (!Directory.Exists(worktreeRoot))
            Directory.CreateDirectory(worktreeRoot);

        // If the worktree path already exists, fail loud rather than silently
        // reuse — a stale worktree usually means the previous run's state
        // should be inspected, not clobbered.
        if (Directory.Exists(worktreePath))
            throw new InvalidOperationException(
                $"Worktree path already exists: {worktreePath}. Remove it with `git worktree remove {worktreePath}` first.");

        RunGit(absTarget, "worktree", "add", worktreePath, "-b", branch);
        return (worktreePath, branch);
    }

    // Hard ceiling on any git invocation. `git worktree add` is normally
    // sub-second; minutes means something on the OS side is wedged
    // (antivirus, credential helper waiting on a hidden window, stale
    // .git/worktrees lock). Surface as a rejection rather than hang the
    // MCP request forever.
    const int GitTimeoutMs = 30_000;

    static void RunGit(string cwd, params string[] args)
    {
        var argDisplay = string.Join(' ', args);
        ClankerLog.Info($"git: invoking `git {argDisplay}` cwd={cwd}");

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

        // Drain both streams concurrently — reading them serially with
        // ReadToEnd risks a pipe-buffer deadlock if git fills the
        // not-yet-being-read stream (4KB on Windows).
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(GitTimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            ClankerLog.Error($"git: TIMEOUT after {GitTimeoutMs}ms `git {argDisplay}` cwd={cwd}");
            throw new InvalidOperationException(
                $"git {argDisplay} timed out after {GitTimeoutMs / 1000}s and was killed");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
        {
            ClankerLog.Error($"git: failed exit={proc.ExitCode} `git {argDisplay}` stderr={stderr.Trim()}");
            throw new InvalidOperationException(
                $"git {argDisplay} failed (exit {proc.ExitCode}): {stderr.Trim()}");
        }

        ClankerLog.Info($"git: completed `git {argDisplay}` exit=0");
    }
}
