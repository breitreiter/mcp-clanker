using System.ClientModel;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Embeddings;

namespace Imp.Infrastructure;

// Embedding client for the substrate. Locked to a single provider per
// rules/embedding-provider.md — no per-call provider selection, no
// fallback. Mixing vector spaces silently corrupts the substrate cache,
// so failure is fail-closed by design.
//
// Config lives in appsettings.json under "EmbeddingProvider":
//   Endpoint:               OpenAI-compat base URL (e.g. http://imp:8081/v1)
//   ApiKey:                 token if the server enforces one; "local" is
//                           fine for unauthenticated local servers
//   Model:                  served model name; llama.cpp typically ignores
//                           this and uses whatever's loaded, but the SDK
//                           requires the field
//   NetworkTimeoutSeconds:  optional, default 60s
public static class Embeddings
{
    public static EmbeddingClient Create(IConfiguration root)
    {
        var section = root.GetSection("EmbeddingProvider");
        var endpoint = Required(section, "Endpoint");
        var apiKey = Required(section, "ApiKey");
        var model = Required(section, "Model");

        var timeoutSeconds = int.TryParse(section["NetworkTimeoutSeconds"], out var t) && t > 0 ? t : 60;

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        return new EmbeddingClient(
            model: model,
            credential: new ApiKeyCredential(apiKey),
            options: options);
    }

    public static async Task<float[]> EmbedAsync(EmbeddingClient client, string input)
    {
        var result = await client.GenerateEmbeddingAsync(input);
        return result.Value.ToFloats().ToArray();
    }

    // Single round-trip for a batch of inputs. OpenAI-compat servers
    // (llama.cpp, vllm, etc.) accept `input: []` and return embeddings in
    // request order. Use this for the tidy cache-refresh pass.
    public static async Task<float[][]> EmbedBatchAsync(EmbeddingClient client, IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0) return Array.Empty<float[]>();
        var result = await client.GenerateEmbeddingsAsync(inputs);
        var output = new float[inputs.Count][];
        for (int i = 0; i < result.Value.Count; i++)
            output[i] = result.Value[i].ToFloats().ToArray();
        return output;
    }

    static string Required(IConfiguration cfg, string key) =>
        cfg[key] ?? throw new InvalidOperationException(
            $"EmbeddingProvider is missing required config key '{key}'.");
}
