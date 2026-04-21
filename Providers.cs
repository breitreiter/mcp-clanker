using Anthropic.SDK;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Mscc.GenerativeAI.Microsoft;

namespace McpClanker;

// Compile-time dispatch over the providers nb supports. Each section in
// ChatProviders[] has a Name that matches a case below. Mirrors nb's provider
// code but drops the IChatClientProvider interface and AssemblyLoadContext
// machinery — we don't need runtime plugin loading.
public static class Providers
{
    public static IChatClient Create(IConfiguration root)
    {
        var active = root["ActiveProvider"]
            ?? throw new InvalidOperationException("ActiveProvider is not set in configuration.");

        var section = root.GetSection("ChatProviders").GetChildren()
            .FirstOrDefault(p => string.Equals(p["Name"], active, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"ActiveProvider '{active}' not found in ChatProviders[].");

        return active.ToLowerInvariant() switch
        {
            "azurefoundry" => CreateAzureFoundry(section),
            "azureopenai" => CreateAzureOpenAI(section),
            "openai" => CreateOpenAI(section),
            "anthropic" => CreateAnthropic(section),
            "gemini" => CreateGemini(section),
            _ => throw new InvalidOperationException($"Unknown provider: {active}"),
        };
    }

    static IChatClient CreateAzureFoundry(IConfiguration cfg)
    {
        var endpoint = Required(cfg, "Endpoint");
        var apiKey = Required(cfg, "ApiKey");
        var model = Required(cfg, "Model");

        // Accept either the resource root or the full deployment URL —
        // the Azure SDK only wants the resource base (scheme + host).
        var parsed = new Uri(endpoint);
        var baseUri = new Uri($"{parsed.Scheme}://{parsed.Authority}/");

        var options = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_03_01_Preview);
        var azureClient = new AzureOpenAIClient(baseUri, new AzureKeyCredential(apiKey), options);
        return azureClient.GetOpenAIResponseClient(model).AsIChatClient();
    }

    static IChatClient CreateAzureOpenAI(IConfiguration cfg)
    {
        var endpoint = Required(cfg, "Endpoint");
        var apiKey = Required(cfg, "ApiKey");
        var deployment = cfg["ChatDeploymentName"] ?? cfg["Model"] ?? "o4-mini";

        var options = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_03_01_Preview);
        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey), options);
        return azureClient.GetChatClient(deployment).AsIChatClient();
    }

    static IChatClient CreateOpenAI(IConfiguration cfg)
    {
        var apiKey = Required(cfg, "ApiKey");
        var model = cfg["Model"] ?? "gpt-4o-mini";
        return new OpenAI.Chat.ChatClient(model, apiKey).AsIChatClient();
    }

    static IChatClient CreateAnthropic(IConfiguration cfg)
    {
        var apiKey = Required(cfg, "ApiKey");
        var model = cfg["Model"] ?? Anthropic.SDK.Constants.AnthropicModels.Claude37Sonnet;
        var client = new AnthropicClient(apiKey);
        return new ChatClientBuilder(client.Messages)
            .ConfigureOptions(o =>
            {
                o.ModelId = model;
                // Anthropic's API requires max_tokens; MEA doesn't set a default.
                o.MaxOutputTokens ??= 4096;
            })
            .Build();
    }

    static IChatClient CreateGemini(IConfiguration cfg)
    {
        var apiKey = Required(cfg, "ApiKey");
        var model = cfg["Model"] ?? "gemini-2.0-flash-exp";
        return new GeminiChatClient(apiKey, model);
    }

    static string Required(IConfiguration cfg, string key) =>
        cfg[key] ?? throw new InvalidOperationException(
            $"Provider '{cfg["Name"]}' is missing required config key '{key}'.");
}
