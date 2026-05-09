using System.Text;
using Microsoft.Extensions.AI;

namespace Imp;

// First model-driven render in the wiki pipeline. Item 11a of
// project/wiki-plan.md.
//
// Single-completion call: gathers each wiki page's source_path / status /
// synthesis_summary into a markdown table, hands the table to an
// orchestrator-role chat client (Wiki:Provider), and returns the body
// markdown the model wrote. WikiIndexRenderer wraps it with the page table
// and footer; WikiIndexRenderer keeps a deterministic fallback that's used
// if this call throws.
//
// Light-orchestrator pattern: one well-bounded model call at one decision
// point. No tools, no agentic loop. Cost is one chat completion per
// `imp wiki` invocation — negligible at Sonnet/Opus rates for an interactive
// command.

public static class WikiIndexSynthesizer
{
    const int MaxOutputTokens = 1500;

    public static async Task<string> RenderBodyAsync(
        IChatClient chat,
        IReadOnlyList<WikiIndexEntry> entries,
        string repoName,
        CancellationToken ct = default)
    {
        var systemPrompt = LoadSystemPrompt();
        var userPrompt = BuildUserPrompt(entries, repoName);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt),
        };
        var options = new ChatOptions { MaxOutputTokens = MaxOutputTokens };

        var response = await chat.GetResponseAsync(messages, options, ct);
        var text = response.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("Orchestrator returned empty response.");
        return text;
    }

    static string LoadSystemPrompt()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "wiki-index-synthesis.md");
        return File.ReadAllText(path);
    }

    static string BuildUserPrompt(IReadOnlyList<WikiIndexEntry> entries, string repoName)
    {
        var sb = new StringBuilder();
        sb.Append("Repository: ").Append(repoName).Append("\n\n");
        sb.Append("Pages (")
          .Append(entries.Count)
          .Append("):\n\n");
        sb.Append("| display | page_url | status | synthesis_summary |\n");
        sb.Append("|---|---|---|---|\n");
        foreach (var e in entries)
        {
            var fm = e.Frontmatter;
            var summary = fm.SynthesisSummary;
            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = fm.Status switch
                {
                    "oversized" => $"(oversized stub — {fm.SourceBytes ?? 0} bytes exceeds threshold)",
                    "failed" => $"(failed: {fm.Error ?? "unknown error"})",
                    _ => "(no summary)",
                };
            }
            // Display uses cluster slug when present so the model can tell
            // sibling clusters apart; page_url is what the link must point at.
            var display = string.IsNullOrEmpty(fm.ClusterSlug)
                ? e.SourcePath
                : $"{e.SourcePath} / {fm.ClusterSlug}";
            sb.Append("| ").Append(EscapeCell(display))
              .Append(" | ").Append(EscapeCell(e.PageRelativePath))
              .Append(" | ").Append(EscapeCell(fm.Status ?? "unknown"))
              .Append(" | ").Append(EscapeCell(summary))
              .Append(" |\n");
        }
        sb.Append('\n');
        sb.Append("Write the README body for this repository per the system instructions.");
        return sb.ToString();
    }

    static string EscapeCell(string value)
        => value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
}
