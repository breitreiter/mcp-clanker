using System.Text;
using System.Text.RegularExpressions;

namespace Imp;

// Regex content search across the contract's working directory. Ported from
// nb/Shell/GrepTool.cs with two simplifications:
//  1. No ShellEnvironment dependency — path is resolved via workingDirectory
//     passed in (same shape as ReadFile / WriteFile).
//  2. No Microsoft.Extensions.FileSystemGlobbing dependency — file_pattern
//     is a filename-level glob only (`*.cs`, `*Test*.cs`, etc.), converted
//     to a regex inline. Path-qualified globs (`Shell/*.cs`) aren't supported
//     in v1; the model can cd down via `path=` instead.
//
// Skip-dirs list mirrors nb's. Binary-file detection is the same null-byte
// heuristic. Output is truncated to imp's standard 8 KB tool cap.

public static class GrepTool
{
    const int DefaultMaxResults = 100;
    const int MaxLineLength = 200;
    const int BinaryCheckBytes = 8192;

    static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", "__pycache__",
        ".venv", "venv", ".idea", "dist", "build", ".next", ".nuget",
    };

    public static string Grep(
        string pattern,
        string? path,
        string? filePattern,
        bool? caseInsensitive,
        int? maxResults,
        string? outputMode,
        string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "ERROR: pattern is required.";

        var limit = maxResults is > 0 ? maxResults.Value : DefaultMaxResults;
        var filesOnly = string.Equals(outputMode, "files_with_matches", StringComparison.OrdinalIgnoreCase);

        var regexOptions = RegexOptions.Compiled;
        if (caseInsensitive == true) regexOptions |= RegexOptions.IgnoreCase;

        Regex contentRegex;
        try
        {
            contentRegex = new Regex(pattern, regexOptions);
        }
        catch (Exception ex)
        {
            return $"ERROR: invalid regex pattern: {ex.Message}";
        }

        var filePatternRegex = CompileFilePattern(filePattern);

        var searchPath = ResolveInsideCwd(path ?? "", workingDirectory);
        if (searchPath is null)
            return $"ERROR: path '{path}' resolves outside the working directory.";

        if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
            return $"ERROR: path not found: {path ?? "."}";

        var files = File.Exists(searchPath)
            ? new[] { searchPath }
            : EnumerateFilesRecursive(searchPath).Where(f =>
                filePatternRegex is null || filePatternRegex.IsMatch(Path.GetFileName(f))).ToArray();

        if (filesOnly)
            return RunFilesOnly(files, contentRegex, workingDirectory, limit);

        return RunContent(files, contentRegex, workingDirectory, limit);
    }

    static string RunFilesOnly(IEnumerable<string> files, Regex regex, string cwd, int limit)
    {
        var matched = new List<string>();
        foreach (var file in files)
        {
            if (matched.Count >= limit) break;
            if (FileContainsMatch(file, regex))
                matched.Add(Path.GetRelativePath(cwd, file));
        }

        if (matched.Count == 0) return "no matches.";

        var output = string.Join("\n", matched);
        if (matched.Count >= limit)
            output += $"\n\n[Showing first {limit} files. Use max_results to see more.]";
        return output;
    }

    static string RunContent(IEnumerable<string> files, Regex regex, string cwd, int limit)
    {
        var matches = new List<string>();
        var totalMatches = 0;

        foreach (var file in files)
        {
            if (matches.Count >= limit) break;
            SearchFile(file, cwd, regex, matches, ref totalMatches, limit);
        }

        if (matches.Count == 0) return "no matches.";

        var output = string.Join("\n", matches);
        if (totalMatches > limit)
            output += $"\n\n[Showing {limit} of {totalMatches} matches. Use max_results to see more.]";
        return output;
    }

    static void SearchFile(string filePath, string cwd, Regex regex, List<string> matches, ref int totalMatches, int limit)
    {
        try
        {
            if (IsBinaryFile(filePath)) return;

            var relativePath = Path.GetRelativePath(cwd, filePath);
            var lineNumber = 0;
            foreach (var line in File.ReadLines(filePath))
            {
                lineNumber++;
                if (regex.IsMatch(line))
                {
                    totalMatches++;
                    if (matches.Count < limit)
                    {
                        var displayLine = line.Length > MaxLineLength
                            ? line[..MaxLineLength] + "…"
                            : line;
                        matches.Add($"{relativePath}:{lineNumber}: {displayLine}");
                    }
                }
            }
        }
        catch
        {
            // Skip files we can't read.
        }
    }

    static bool FileContainsMatch(string filePath, Regex regex)
    {
        try
        {
            if (IsBinaryFile(filePath)) return false;
            foreach (var line in File.ReadLines(filePath))
                if (regex.IsMatch(line)) return true;
            return false;
        }
        catch { return false; }
    }

    static bool IsBinaryFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var length = Math.Min(BinaryCheckBytes, (int)Math.Min(stream.Length, int.MaxValue));
            if (length == 0) return false;
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch
        {
            return true;
        }
    }

    static IEnumerable<string> EnumerateFilesRecursive(string directory)
    {
        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var file in files)
                yield return file;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (!SkipDirectories.Contains(name))
                    stack.Push(sub);
            }
        }
    }

    static Regex? CompileFilePattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    // Duplicate of Tools.ResolveInsideCwd — kept local to avoid coupling.
    // Consolidate if a third file tool needs the same helper.
    static string? ResolveInsideCwd(string path, string cwd)
    {
        var absCwd = Path.GetFullPath(cwd);
        var candidate = string.IsNullOrEmpty(path)
            ? absCwd
            : Path.IsPathRooted(path) ? path : Path.Combine(absCwd, path);
        var resolved = Path.GetFullPath(candidate);
        var normalizedCwd = absCwd.EndsWith(Path.DirectorySeparatorChar) ? absCwd : absCwd + Path.DirectorySeparatorChar;
        if (resolved == absCwd || resolved.StartsWith(normalizedCwd, StringComparison.Ordinal))
            return resolved;
        return null;
    }
}
