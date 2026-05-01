using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Imp;

// Resolves which bash binary the Host-mode bash tool should invoke,
// and supplies a system-prompt fragment alerting the executor to any
// shell-specific quirks (currently: MSYS path translation on Windows).
//
// On Linux/macOS the answer is /bin/bash. On Windows it's Git for
// Windows' bash.exe — PowerShell is intentionally not supported,
// because models name-confuse the "bash" tool with PowerShell idioms
// and emit broken commands. Same rationale as sibling project nb's
// Shell/ShellEnvironment.cs (the inspiration for this file).
//
// Docker mode never calls Resolve() — it spawns `docker` directly and
// the inner `/bin/bash` runs inside the Linux container, so the host
// shell is irrelevant.

public static class ShellResolver
{
    static readonly Lazy<string> _path = new(ResolveCore);

    public static string Resolve() => _path.Value;

    static string ResolveCore()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var bash = FindGitBash();
            if (bash == null)
                throw new InvalidOperationException(
                    "imp requires Git Bash on Windows for Sandbox.Mode=Host, " +
                    "but bash.exe was not found. Install Git for Windows from " +
                    "https://git-scm.com/download/win (typical path: " +
                    "C:\\Program Files\\Git\\bin\\bash.exe), or switch " +
                    "Sandbox.Mode to \"Docker\" in appsettings.json.");
            return bash;
        }
        return "/bin/bash";
    }

    static string? FindGitBash()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;

        var onPath = WhereBash();
        if (onPath != null && onPath.EndsWith("bash.exe", StringComparison.OrdinalIgnoreCase))
            return onPath;
        return null;
    }

    static string? WhereBash()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0) return null;
            var first = output.Split('\n')[0].Trim();
            return string.IsNullOrEmpty(first) ? null : first;
        }
        catch { return null; }
    }

    // System-prompt fragment about shell-specific quirks the executor
    // needs to know. Empty unless we're on Windows in Host mode —
    // Docker-mode commands run inside a Linux container, so MSYS
    // translation is not in play.
    public static string GetExecutorEnvNote(SandboxMode mode)
    {
        if (mode != SandboxMode.Host) return "";
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "";

        return "\n## Shell environment\n\n"
            + "The bash tool runs Git Bash (MSYS2) on Windows. Use POSIX syntax — "
            + "not PowerShell cmdlets or switches like -Force. MSYS auto-translates "
            + "Unix-style paths to Windows paths (e.g. /c/Users/... ↔ C:\\Users\\...). "
            + "If an argument that looks like a path is being mangled, prefix the "
            + "command with MSYS_NO_PATHCONV=1.\n";
    }
}
