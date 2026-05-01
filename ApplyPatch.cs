namespace Imp;

// Codex's apply_patch format — model-native unified-diff-with-envelope that
// gpt-5.x is specifically trained on. Ported from nb's Shell/ApplyPatch/
// with three adaptations:
//   1. No FileReadTracker — imp runs in a fresh worktree with no
//      concurrent mutation, and we trust the SeekSequence cascade to
//      report a clean "chunk not found" when the model hallucinates.
//   2. Resolve() clamps paths inside workingDirectory (matches ReadFile
//      / WriteFile / GrepTool's security posture).
//   3. Caller (Tools.cs) records touched files on ExecutorState so the
//      POW's files_changed list fills correctly.
//
// Flow: BuildPreview (validate, compute replacements, no writes)
//       → Apply (write to disk, moves/deletes).
// The split is preserved from nb even though v1 calls them back-to-back
// inline — it keeps the door open for a future human-approval flow.

// --- models ---

public abstract record FileOp(string Path);
public sealed record AddFile(string Path, string Content) : FileOp(Path);
public sealed record DeleteFile(string Path) : FileOp(Path);
public sealed record UpdateFile(string Path, string? MoveTo, List<UpdateChunk> Chunks) : FileOp(Path);

public sealed record UpdateChunk(
    List<string> ContextHeaders,
    List<string> OldLines,
    List<string> NewLines,
    bool IsEndOfFile);

public sealed class PatchParseException : Exception
{
    public int LineNumber { get; }
    public PatchParseException(string message, int lineNumber) : base($"line {lineNumber}: {message}")
    {
        LineNumber = lineNumber;
    }
}

public sealed class PatchApplyException : Exception
{
    public PatchApplyException(string message) : base(message) { }
}

public enum FileOpKind { Add, Update, UpdateAndMove, Delete }

public sealed record FilePreview(
    string OriginalPath,
    string FinalPath,
    FileOpKind Kind,
    int OldLineCount,
    int NewLineCount,
    string? ComputedContent);

public sealed record PatchPreview(List<FilePreview> Files);

// --- parser ---

public static class PatchParser
{
    const string BeginPatch = "*** Begin Patch";
    const string EndPatch = "*** End Patch";
    const string AddPrefix = "*** Add File: ";
    const string DeletePrefix = "*** Delete File: ";
    const string UpdatePrefix = "*** Update File: ";
    const string MovePrefix = "*** Move to: ";
    const string EndOfFile = "*** End of File";

    public static List<FileOp> Parse(string input)
    {
        var lines = input.Replace("\r\n", "\n").Split('\n');
        var i = FindBegin(lines);
        if (i < 0)
            throw new PatchParseException("patch must start with '*** Begin Patch'", 1);
        i++;

        var ops = new List<FileOp>();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line == EndPatch) return ops;

            if (line.StartsWith(AddPrefix))
            {
                var path = line[AddPrefix.Length..];
                i++;
                ops.Add(ParseAdd(path, lines, ref i));
            }
            else if (line.StartsWith(DeletePrefix))
            {
                var path = line[DeletePrefix.Length..];
                ops.Add(new DeleteFile(path));
                i++;
            }
            else if (line.StartsWith(UpdatePrefix))
            {
                var path = line[UpdatePrefix.Length..];
                i++;
                ops.Add(ParseUpdate(path, lines, ref i));
            }
            else if (string.IsNullOrEmpty(line) && i == lines.Length - 1)
            {
                i++;
            }
            else
            {
                throw new PatchParseException($"unexpected line: {Truncate(line)}", i + 1);
            }
        }

        throw new PatchParseException("patch is missing '*** End Patch'", lines.Length);
    }

    static int FindBegin(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
            if (lines[i] == BeginPatch) return i;
        return -1;
    }

    static AddFile ParseAdd(string path, string[] lines, ref int i)
    {
        var body = new List<string>();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsFileHeader(line) || line == EndPatch) break;
            if (line.Length == 0)
            {
                body.Add("");
                i++;
                continue;
            }
            if (line[0] != '+')
                throw new PatchParseException($"Add File body must start with '+': {Truncate(line)}", i + 1);
            body.Add(line[1..]);
            i++;
        }
        return new AddFile(path, string.Join("\n", body));
    }

    static UpdateFile ParseUpdate(string path, string[] lines, ref int i)
    {
        string? moveTo = null;
        if (i < lines.Length && lines[i].StartsWith(MovePrefix))
        {
            moveTo = lines[i][MovePrefix.Length..];
            i++;
        }

        var chunks = new List<UpdateChunk>();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsFileHeader(line) || line == EndPatch) break;

            var chunk = ParseChunk(lines, ref i);
            if (chunk is not null) chunks.Add(chunk);
        }

        if (chunks.Count == 0)
            throw new PatchParseException($"Update File '{path}' has no chunks", i);

        return new UpdateFile(path, moveTo, chunks);
    }

    static UpdateChunk? ParseChunk(string[] lines, ref int i)
    {
        var headers = new List<string>();
        while (i < lines.Length && lines[i].StartsWith("@@"))
        {
            var raw = lines[i];
            var header = raw.Length > 2 && raw[2] == ' ' ? raw[3..] : "";
            if (!string.IsNullOrEmpty(header)) headers.Add(header);
            i++;
        }

        var oldLines = new List<string>();
        var newLines = new List<string>();
        var isEof = false;
        var sawChange = false;

        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsFileHeader(line) || line == EndPatch) break;
            if (line.StartsWith("@@")) break;

            if (line == EndOfFile)
            {
                isEof = true;
                i++;
                break;
            }

            if (line.Length == 0)
            {
                oldLines.Add("");
                newLines.Add("");
                sawChange = true;
                i++;
                continue;
            }

            var sigil = line[0];
            var body = line[1..];
            if (sigil == ' ')
            {
                oldLines.Add(body);
                newLines.Add(body);
            }
            else if (sigil == '-')
            {
                oldLines.Add(body);
            }
            else if (sigil == '+')
            {
                newLines.Add(body);
            }
            else
            {
                break;
            }
            sawChange = true;
            i++;
        }

        if (!sawChange && headers.Count == 0) return null;
        return new UpdateChunk(headers, oldLines, newLines, isEof);
    }

    static bool IsFileHeader(string line) =>
        line.StartsWith(AddPrefix) || line.StartsWith(DeletePrefix) || line.StartsWith(UpdatePrefix);

    static string Truncate(string s) => s.Length > 80 ? s[..80] + "…" : s;
}

// --- applier ---

public static class PatchApplier
{
    public static PatchPreview BuildPreview(List<FileOp> ops, string cwd)
    {
        var files = new List<FilePreview>();

        foreach (var op in ops)
        {
            var fullPath = Resolve(op.Path, cwd);

            switch (op)
            {
                case AddFile add:
                    if (File.Exists(fullPath))
                        throw new PatchApplyException($"Add File failed: '{op.Path}' already exists");
                    files.Add(new FilePreview(op.Path, fullPath, FileOpKind.Add, 0, CountLines(add.Content), add.Content));
                    break;

                case DeleteFile:
                    if (!File.Exists(fullPath))
                        throw new PatchApplyException($"Delete File failed: '{op.Path}' does not exist");
                    files.Add(new FilePreview(op.Path, fullPath, FileOpKind.Delete, CountLines(File.ReadAllText(fullPath)), 0, null));
                    break;

                case UpdateFile upd:
                    if (!File.Exists(fullPath))
                        throw new PatchApplyException($"Update File failed: '{op.Path}' does not exist");

                    var original = File.ReadAllText(fullPath);
                    var (updated, oldLc, newLc) = ApplyUpdate(op.Path, original, upd.Chunks);

                    var finalPath = upd.MoveTo is not null ? Resolve(upd.MoveTo, cwd) : fullPath;
                    var kind = upd.MoveTo is not null ? FileOpKind.UpdateAndMove : FileOpKind.Update;
                    files.Add(new FilePreview(op.Path, finalPath, kind, oldLc, newLc, updated));
                    break;
            }
        }

        return new PatchPreview(files);
    }

    public static void Apply(PatchPreview preview, List<FileOp> ops, string cwd)
    {
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            var fp = preview.Files[i];

            switch (op)
            {
                case AddFile:
                {
                    var dir = Path.GetDirectoryName(fp.FinalPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(fp.FinalPath, fp.ComputedContent ?? "");
                    break;
                }
                case DeleteFile:
                    File.Delete(fp.FinalPath);
                    break;
                case UpdateFile upd:
                {
                    var originalFull = Resolve(op.Path, cwd);
                    File.WriteAllText(originalFull, fp.ComputedContent ?? "");
                    if (upd.MoveTo is not null)
                    {
                        var dir = Path.GetDirectoryName(fp.FinalPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        File.Move(originalFull, fp.FinalPath, overwrite: false);
                    }
                    break;
                }
            }
        }
    }

    static (string Content, int OldLineCount, int NewLineCount) ApplyUpdate(
        string relPath, string original, List<UpdateChunk> chunks)
    {
        var fileUsesCrlf = original.Contains("\r\n");
        var normalized = fileUsesCrlf ? original.Replace("\r\n", "\n") : original;

        var endsWithNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (endsWithNewline && lines.Count > 0 && lines[^1] == "")
            lines.RemoveAt(lines.Count - 1);

        var cursor = 0;
        var replacements = new List<(int Index, int OldLen, List<string> NewLines)>();

        foreach (var chunk in chunks)
        {
            foreach (var header in chunk.ContextHeaders)
            {
                var idx = SeekSequence.Find(lines, new[] { header }, cursor);
                if (idx < 0)
                    throw new PatchApplyException($"Update File '{relPath}': failed to find context header '@@ {Truncate(header)}'");
                cursor = idx + 1;
            }

            int matchIndex;
            if (chunk.OldLines.Count == 0)
            {
                matchIndex = lines.Count;
            }
            else
            {
                if (chunk.IsEndOfFile)
                {
                    var tailStart = Math.Max(cursor, lines.Count - chunk.OldLines.Count);
                    matchIndex = SeekSequence.Find(lines, chunk.OldLines, tailStart);
                    if (matchIndex < 0)
                        matchIndex = SeekSequence.Find(lines, chunk.OldLines, cursor);
                }
                else
                {
                    matchIndex = SeekSequence.Find(lines, chunk.OldLines, cursor);
                }

                if (matchIndex < 0 && chunk.OldLines.Count > 0 && chunk.OldLines[^1] == "")
                {
                    var trimmed = chunk.OldLines.Take(chunk.OldLines.Count - 1).ToList();
                    matchIndex = SeekSequence.Find(lines, trimmed, cursor);
                }

                if (matchIndex < 0)
                {
                    var preview = string.Join("\n", chunk.OldLines.Take(3));
                    throw new PatchApplyException(
                        $"Update File '{relPath}': failed to locate chunk. Expected lines not found:\n{preview}");
                }
            }

            replacements.Add((matchIndex, chunk.OldLines.Count, chunk.NewLines));
            cursor = matchIndex + chunk.OldLines.Count;
        }

        replacements.Sort((a, b) => a.Index.CompareTo(b.Index));

        var result = new List<string>(lines.Count + 32);
        var pos = 0;
        var oldLineCount = 0;
        var newLineCount = 0;
        foreach (var (idx, oldLen, newLines) in replacements)
        {
            while (pos < idx) result.Add(lines[pos++]);
            result.AddRange(newLines);
            pos = idx + oldLen;
            oldLineCount += oldLen;
            newLineCount += newLines.Count;
        }
        while (pos < lines.Count) result.Add(lines[pos++]);

        var sep = fileUsesCrlf ? "\r\n" : "\n";
        var content = string.Join(sep, result);
        if (endsWithNewline) content += sep;

        return (content, oldLineCount, newLineCount);
    }

    // Clamped path resolution — rejects anything that escapes cwd after
    // Path.GetFullPath normalization (so `../foo` and absolute paths
    // outside cwd both throw).
    static string Resolve(string path, string cwd)
    {
        var absCwd = Path.GetFullPath(cwd);
        var candidate = Path.IsPathRooted(path) ? path : Path.Combine(absCwd, path);
        var resolved = Path.GetFullPath(candidate);
        var normalizedCwd = absCwd.EndsWith(Path.DirectorySeparatorChar) ? absCwd : absCwd + Path.DirectorySeparatorChar;
        if (resolved != absCwd && !resolved.StartsWith(normalizedCwd, StringComparison.Ordinal))
            throw new PatchApplyException($"path '{path}' resolves outside the working directory");
        return resolved;
    }

    static int CountLines(string s) => s.Length == 0 ? 0 : s.Split('\n').Length;

    static string Truncate(string s) => s.Length > 80 ? s[..80] + "…" : s;
}
