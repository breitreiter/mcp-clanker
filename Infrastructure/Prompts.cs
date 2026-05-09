using Imp.Build;

namespace Imp.Infrastructure;

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

    public static string LoadSystemPrompt(string? providerName, Contract contract, SandboxMode sandboxMode)
    {
        var template = LoadTemplate(providerName);
        var body = template.Replace(ContractToken, contract.RawMarkdown);
        return body + ShellResolver.GetExecutorEnvNote(sandboxMode);
    }

    public static string LoadCloseoutPrompt()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "closeout.md");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Closeout prompt template not found at {path}.");
        return File.ReadAllText(path);
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
