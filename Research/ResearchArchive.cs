using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using Imp.Build;

namespace Imp.Research;

// Writer for the per-research sidecar archive at
//   <parent>/<repo>.researches/R-NNN-<slug>/
// containing:
//   brief.md         — verbatim source markdown of the brief
//   report.json      — the full ResearchReport
//   findings.jsonl   — one finding per line, for cheap greppability
//   transcript.md    — rendered by TranscriptRenderer (written by the executor)
//   trace.jsonl      — raw event stream (written by the executor)
//   meta.json        — small index { created_at, brief_hash, model, mode, tags, sources_count }
//
// The plan is explicit that this archive is the answer to "have you researched
// this before?" — the parent retrieves over it, imp does the write.

public static class ResearchArchive
{
    public static string RootFor(string repoRoot)
    {
        var absRoot = Path.GetFullPath(repoRoot);
        var name = Path.GetFileName(absRoot.TrimEnd(Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(absRoot)
            ?? throw new InvalidOperationException($"Repo root '{absRoot}' has no parent directory.");
        return Path.Combine(parent, $"{name}.researches");
    }

    public static string DirectoryFor(string repoRoot, TaskDescriptor descriptor)
        => Path.Combine(RootFor(repoRoot), $"{descriptor.ResearchId}-{descriptor.Slug}");

    public static void WriteBrief(string archiveDir, TaskDescriptor descriptor)
    {
        Directory.CreateDirectory(archiveDir);
        File.WriteAllText(Path.Combine(archiveDir, "brief.md"), descriptor.SourceMarkdown);
    }

    public static void WriteReport(string archiveDir, ResearchReport report)
    {
        Directory.CreateDirectory(archiveDir);
        var json = ResearchReportJson.Serialize(report);
        File.WriteAllText(Path.Combine(archiveDir, "report.json"), json);
        WriteFindingsJsonl(archiveDir, report.Findings);
    }

    static void WriteFindingsJsonl(string archiveDir, IReadOnlyList<Finding> findings)
    {
        // One JSON object per line, no pretty-printing — that's the point of
        // the JSONL form. The parent can grep over it, pipe it through jq,
        // or load it into a vector store without parsing the whole report.
        var compact = new JsonSerializerOptions(ResearchReportJson.Options)
        {
            WriteIndented = false,
        };
        using var sw = new StreamWriter(
            Path.Combine(archiveDir, "findings.jsonl"),
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var f in findings)
            sw.WriteLine(JsonSerializer.Serialize(f, compact));
    }

    public static void WriteMeta(
        string archiveDir,
        TaskDescriptor descriptor,
        string mode,
        string? providerName,
        string? modelName,
        ResearchReport? report,
        TerminalState terminal)
    {
        Directory.CreateDirectory(archiveDir);
        var meta = new ArchiveMeta(
            ResearchId: descriptor.ResearchId,
            Slug: descriptor.Slug,
            Mode: mode,
            CreatedAt: DateTime.UtcNow,
            Provider: providerName,
            Model: modelName,
            Question: descriptor.Question,
            BriefHash: ShortHash(descriptor.SourceMarkdown),
            FindingsCount: report?.Findings.Count ?? 0,
            CitationsCount: report?.Findings.Sum(f => f.Citations.Count) ?? 0,
            Terminal: terminal);

        var path = Path.Combine(archiveDir, "meta.json");
        File.WriteAllText(path, JsonSerializer.Serialize(meta, MetaOptions));
    }

    static readonly JsonSerializerOptions MetaOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static string ShortHash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    record ArchiveMeta(
        string ResearchId,
        string Slug,
        string Mode,
        DateTime CreatedAt,
        string? Provider,
        string? Model,
        string Question,
        string BriefHash,
        int FindingsCount,
        int CitationsCount,
        TerminalState Terminal);
}
