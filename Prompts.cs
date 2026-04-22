namespace McpClanker;

// Loads the executor's system prompt from the Prompts/ directory alongside
// the executable. Fallback chain: Prompts/<provider>.md → Prompts/default.md.
// One interpolation token: {{CONTRACT}}, replaced with the contract markdown.
//
// Keeping prompt templates as markdown files on disk (rather than C# string
// literals) so we can iterate on prompts without recompiling and so prompt
// diffs show up as prompt changes in git, not code changes.

public static class Prompts
{
    const string ContractToken = "{{CONTRACT}}";

    public static string LoadSystemPrompt(string? providerName, Contract contract)
    {
        var template = LoadTemplate(providerName);
        return template.Replace(ContractToken, contract.RawMarkdown);
    }

    static string LoadTemplate(string? providerName)
    {
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "Prompts");

        if (!string.IsNullOrEmpty(providerName))
        {
            var providerPath = Path.Combine(promptsDir, $"{providerName}.md");
            if (File.Exists(providerPath))
                return File.ReadAllText(providerPath);
        }

        var defaultPath = Path.Combine(promptsDir, "default.md");
        if (!File.Exists(defaultPath))
            throw new FileNotFoundException(
                $"No prompt template found. Expected {defaultPath} (and optionally {providerName}.md).");

        return File.ReadAllText(defaultPath);
    }
}
