using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpClanker;

public class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--ping")
        {
            await RunPing(args.Length > 1 ? args[1] : null);
            return;
        }

        if (args.Length > 0 && args[0] == "--ping-tools")
        {
            await RunPingTools(args.Length > 1 ? args[1] : null);
            return;
        }

        var builder = Host.CreateApplicationBuilder(args);

        // Host.CreateApplicationBuilder loads appsettings.json from the current
        // working directory by default. Claude Code launches us with cwd set to
        // whatever repo it's operating on, so also probe the executable's own
        // directory as a fallback — that's where the build output copy lives.
        builder.Configuration.AddJsonFile(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            optional: true,
            reloadOnChange: false);

        // MCP stdio: logging goes to stderr so stdout stays clean for protocol frames.
        builder.Logging.AddConsole(o =>
        {
            o.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services.AddSingleton<IChatClient>(sp =>
            Providers.Create(sp.GetRequiredService<IConfiguration>()));

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithResources(CreateResources());

        await builder.Build().RunAsync();
    }

    static async Task RunPing(string? provider)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile("appsettings.json", optional: true);

        // Allow CLI override: dotnet run -- --ping AzureFoundry
        if (!string.IsNullOrEmpty(provider))
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ActiveProvider"] = provider,
            });
        }

        var config = builder.Build();
        Console.Error.WriteLine($"[ping] ActiveProvider = {config["ActiveProvider"]}");

        var client = Providers.Create(config);
        var response = await client.GetResponseAsync(
            "Reply with exactly the word 'pong' and nothing else.",
            new ChatOptions { MaxOutputTokens = 2000 });
        Console.Error.WriteLine($"[ping] FinishReason = {response.FinishReason}");
        Console.Error.WriteLine($"[ping] Usage = in:{response.Usage?.InputTokenCount} out:{response.Usage?.OutputTokenCount} total:{response.Usage?.TotalTokenCount}");
        Console.Error.WriteLine($"[ping] Messages = {response.Messages.Count}, Contents across messages = {response.Messages.Sum(m => m.Contents.Count)}");
        foreach (var msg in response.Messages)
            foreach (var c in msg.Contents)
                Console.Error.WriteLine($"[ping]   {msg.Role} content: {c.GetType().Name}");
        Console.WriteLine(response.Text);
    }

    // Verifies multi-turn tool-calling round-trips correctly against the active
    // provider. Specifically checks that reasoning-model responses (TextReasoningContent)
    // don't break the follow-up turn after a tool call. If this works, the inner
    // executor loop is safe to build on the same pattern.
    static async Task RunPingTools(string? provider)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile("appsettings.json", optional: true);

        if (!string.IsNullOrEmpty(provider))
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ActiveProvider"] = provider,
            });
        }

        var config = builder.Build();
        Console.Error.WriteLine($"[ping-tools] ActiveProvider = {config["ActiveProvider"]}");

        var client = Providers.Create(config);

        var echoTool = AIFunctionFactory.Create(
            (string value) => $"echoed: {value}",
            name: "echo",
            description: "Echoes the given value back with an 'echoed: ' prefix.");

        var options = new ChatOptions
        {
            MaxOutputTokens = 2000,
            Tools = new List<AITool> { echoTool },
        };

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Call the echo tool with value='hello world'. After the tool returns, reply with exactly the tool's output and nothing else."),
        };

        Console.Error.WriteLine("[ping-tools] --- Turn 1 (expect tool call) ---");
        var r1 = await client.GetResponseAsync(history, options);
        DescribeResponse("r1", r1);

        var toolCalls = r1.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .ToList();

        if (toolCalls.Count == 0)
        {
            Console.Error.WriteLine("[ping-tools] FAIL: no tool call on turn 1.");
            Console.WriteLine(r1.Text);
            return;
        }

        // Echo assistant messages back into history for turn 2.
        foreach (var msg in r1.Messages)
            history.Add(msg);

        var toolResults = new List<AIContent>();
        foreach (var call in toolCalls)
        {
            var valueArg = call.Arguments?.TryGetValue("value", out var v) == true ? v?.ToString() ?? "" : "";
            var result = $"echoed: {valueArg}";
            Console.Error.WriteLine($"[ping-tools] Tool call: {call.Name}(value={valueArg}) -> {result}");
            toolResults.Add(new FunctionResultContent(call.CallId, result));
        }

        history.Add(new ChatMessage(ChatRole.Tool, toolResults));

        Console.Error.WriteLine("[ping-tools] --- Turn 2 (expect final text) ---");
        var r2 = await client.GetResponseAsync(history, options);
        DescribeResponse("r2", r2);

        Console.WriteLine(r2.Text);
    }

    static void DescribeResponse(string label, ChatResponse response)
    {
        Console.Error.WriteLine($"[ping-tools] {label}.FinishReason = {response.FinishReason}");
        Console.Error.WriteLine($"[ping-tools] {label}.Usage = in:{response.Usage?.InputTokenCount} out:{response.Usage?.OutputTokenCount}");
        Console.Error.WriteLine($"[ping-tools] {label}.Messages = {response.Messages.Count}");
        foreach (var msg in response.Messages)
            foreach (var c in msg.Contents)
                Console.Error.WriteLine($"[ping-tools]   {msg.Role} content: {c.GetType().Name}");
    }

    static IEnumerable<McpServerResource> CreateResources()
    {
        var templatesDir = Path.Combine(AppContext.BaseDirectory, "Templates");

        yield return McpServerResource.Create(
            () => File.ReadAllText(Path.Combine(templatesDir, "contract.md")),
            new McpServerResourceCreateOptions
            {
                UriTemplate = "template://contract",
                Name = "Contract template",
                Description = "Markdown skeleton for a new T-NNN contract file.",
                MimeType = "text/markdown",
            });

        yield return McpServerResource.Create(
            () => File.ReadAllText(Path.Combine(templatesDir, "proof-of-work.json")),
            new McpServerResourceCreateOptions
            {
                UriTemplate = "template://proof-of-work",
                Name = "Proof-of-work template",
                Description = "Example shape of the structured artifact returned by build().",
                MimeType = "application/json",
            });
    }
}
