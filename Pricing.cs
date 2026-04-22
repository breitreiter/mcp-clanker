namespace McpClanker;

// Estimates USD cost from token counts and model name.
// Rates are hand-entered placeholders; accuracy is not guaranteed.

public static class Pricing
{
    // Rates per 1,000,000 tokens (input, output). Hand-entered placeholders.
    static readonly Dictionary<string, (decimal inputPerMTok, decimal outputPerMTok)> Rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.1-codex-mini"] = (0.25m, 2.00m),
        ["gpt-5"] = (1.25m, 10.00m),
        ["claude-sonnet-4-5"] = (3.00m, 15.00m),
        ["gemini-2.0-flash"] = (0.00m, 0.00m),
    };

    public static decimal Estimate(string? modelName, long tokensIn, long tokensOut)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return 0m;

        // Longest-prefix match: find the table key that modelName starts with.
        var match = Rates
            .OrderByDescending(kv => kv.Key.Length)
            .FirstOrDefault(kv => modelName.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase));

        if (match.Key is null)
            return 0m;

        var (inputRate, outputRate) = match.Value;
        var inputCost = (tokensIn / 1_000_000m) * inputRate;
        var outputCost = (tokensOut / 1_000_000m) * outputRate;
        return inputCost + outputCost;
    }
}
