using System.Text;

namespace Imp.Infrastructure;

// Tiny file-and-stderr logger. Persists across CLI invocations so
// failures (especially pre-worktree ones, which never reach trace.jsonl)
// leave a forensic trail even when the host shell doesn't capture stderr.
//
// Path: <AppContext.BaseDirectory>/imp.log — sits next to the DLL.
// Append-only, no rotation. Thread-safe under concurrent Build() calls.
// Logging never throws — silently swallows file/stderr errors.

public static class ImpLog
{
    static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "imp.log");
    static readonly object Lock = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    static void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:O} {level} {message}";
        lock (Lock)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
        try { Console.Error.WriteLine(line); } catch { }
    }
}
