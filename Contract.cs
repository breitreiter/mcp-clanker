using System.Text.RegularExpressions;

namespace McpClanker;

// Parses the markdown contract template from Templates/contract.md into a
// structured form. Lenient by design — missing sections degrade to empty
// rather than throwing. Fail-loud rejections happen at validation time, not
// parse time.

public record Contract(
    string TaskId,
    string Title,
    string Goal,
    IReadOnlyList<ScopeEntry> Scope,
    string ContractBody,
    IReadOnlyList<ContextEntry> Context,
    IReadOnlyList<string> Acceptance,
    IReadOnlyList<string> NonGoals,
    IReadOnlyList<string> DependsOn,
    string RawMarkdown);

public record ScopeEntry(ScopeAction Action, string Path);
public record ContextEntry(string Path, string Note);

public enum ScopeAction { Create, Edit, Delete }

public static class ContractParser
{
    // Title: "## T-NNN: short descriptive title"
    static readonly Regex TitleRx = new(@"^##\s*(T-\d+)\s*:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    // Goal: "**Goal:** text on same line"
    static readonly Regex GoalRx = new(@"\*\*Goal:\*\*\s*(.+?)(?=\r?\n\r?\n|\r?\n\*\*|\z)", RegexOptions.Singleline | RegexOptions.Compiled);

    // Depends on: "**Depends on:** T-001, T-002" or "none"
    static readonly Regex DependsRx = new(@"\*\*Depends on:\*\*\s*(.+?)(?=\r?\n|\z)", RegexOptions.Compiled);

    // Scope bullet: "- create: path" | "- edit: path" | "- delete: path"
    static readonly Regex ScopeLineRx = new(@"^\s*-\s*(create|edit|delete)\s*:\s*(.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Context bullet: "- path/to/file — why it matters"
    static readonly Regex ContextLineRx = new(@"^\s*-\s*(.+?)\s*[—-]\s*(.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Generic bullet: "- anything"
    static readonly Regex BulletRx = new(@"^\s*-\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static Contract Parse(string markdown)
    {
        var title = TitleRx.Match(markdown);
        var taskId = title.Success ? title.Groups[1].Value : "T-???";
        var titleText = title.Success ? title.Groups[2].Value : "(untitled)";

        var goalMatch = GoalRx.Match(markdown);
        var goal = goalMatch.Success ? CleanSingleLine(goalMatch.Groups[1].Value) : "";

        var dependsMatch = DependsRx.Match(markdown);
        var depends = dependsMatch.Success
            ? ParseDependencyList(dependsMatch.Groups[1].Value)
            : Array.Empty<string>();

        var scopeSection = Section(markdown, "Scope");
        var scope = scopeSection is null
            ? new List<ScopeEntry>()
            : ScopeLineRx.Matches(scopeSection)
                .Select(m => new ScopeEntry(ParseScopeAction(m.Groups[1].Value), m.Groups[2].Value.Trim()))
                .ToList();

        var contextSection = Section(markdown, "Context");
        var context = contextSection is null
            ? new List<ContextEntry>()
            : ContextLineRx.Matches(contextSection)
                .Select(m => new ContextEntry(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()))
                .ToList();

        var acceptance = BulletsIn(Section(markdown, "Acceptance"));
        var nonGoals = BulletsIn(Section(markdown, "Non-goals"));
        var contractBody = Section(markdown, "Contract") ?? "";

        return new Contract(
            TaskId: taskId,
            Title: titleText,
            Goal: goal,
            Scope: scope,
            ContractBody: contractBody.Trim(),
            Context: context,
            Acceptance: acceptance,
            NonGoals: nonGoals,
            DependsOn: depends,
            RawMarkdown: markdown);
    }

    // Extract the body of a **Section:** — everything from the header to the
    // next **OtherSection:** header or EOF.
    static string? Section(string markdown, string name)
    {
        var pattern = @$"\*\*{Regex.Escape(name)}:\*\*\s*\r?\n?(.+?)(?=\r?\n\*\*[^*]+:\*\*|\z)";
        var m = Regex.Match(markdown, pattern, RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : null;
    }

    static IReadOnlyList<string> BulletsIn(string? section) =>
        section is null
            ? Array.Empty<string>()
            : BulletRx.Matches(section).Select(m => m.Groups[1].Value.Trim()).ToList();

    static ScopeAction ParseScopeAction(string s) => s.ToLowerInvariant() switch
    {
        "create" => ScopeAction.Create,
        "edit" => ScopeAction.Edit,
        "delete" => ScopeAction.Delete,
        _ => ScopeAction.Edit,
    };

    static IReadOnlyList<string> ParseDependencyList(string s)
    {
        var trimmed = s.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();
        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    static string CleanSingleLine(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}

public record ContractValidation(bool IsValid, string? RejectionReason)
{
    public static ContractValidation Ok() => new(true, null);
    public static ContractValidation Reject(string reason) => new(false, reason);
}

public static class ContractValidator
{
    // Fast fail-before-execute check. Returns Reject if anything is
    // structurally wrong with the contract. Does not run the model.
    public static ContractValidation Validate(Contract contract, string targetRepo)
    {
        if (string.IsNullOrWhiteSpace(contract.Goal))
            return ContractValidation.Reject("Contract has no **Goal:** section.");

        if (contract.Scope.Count == 0)
            return ContractValidation.Reject("Contract has no **Scope:** entries.");

        if (contract.Acceptance.Count == 0)
            return ContractValidation.Reject("Contract has no **Acceptance:** items.");

        // Scope files must exist (for edit/delete) or parent dir must exist (for create).
        foreach (var entry in contract.Scope)
        {
            var full = Path.Combine(targetRepo, entry.Path);
            if (entry.Action == ScopeAction.Edit || entry.Action == ScopeAction.Delete)
            {
                if (!File.Exists(full))
                    return ContractValidation.Reject(
                        $"Scope entry '{entry.Action}: {entry.Path}' references a file that does not exist in {targetRepo}.");
            }
            else if (entry.Action == ScopeAction.Create)
            {
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    return ContractValidation.Reject(
                        $"Scope entry 'create: {entry.Path}' parent directory '{parent}' does not exist.");
            }
        }

        return ContractValidation.Ok();
    }
}
