using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Imp;

// CLI entry point. One process per invocation, scoped to cwd.
//
// Subcommands fall into three groups:
//   build / validate / review     — the core delegation lifecycle
//   list / show / log / template  — read-only inspection of contracts and artifacts
//   ping / ping-tools / render-transcript — diagnostics
//
// Most subcommands delegate to static methods in McpTools. The `review`
// subcommand and the diagnostic helpers are handled here.

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await Dispatch(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"imp: unhandled error: {ex.GetType().Name}: {ex.Message}");
            ImpLog.Error($"unhandled: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    static async Task<int> Dispatch(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return args[0] switch
        {
            "build" => await RunBuild(args[1..]),
            "research" => await RunResearch(args[1..]),
            "wiki" => await RunWiki(args[1..]),
            "validate" => await RunValidate(args[1..]),
            "review" => RunReview(args[1..]),
            "list" => RunList(),
            "show" => RunShow(args[1..]),
            "log" => RunLog(args[1..]),
            "template" => RunTemplate(args[1..]),
            "ping" => await RunPing(args[1..]),
            "ping-tools" => await RunPingTools(args[1..]),
            "render-transcript" => RunRenderTranscript(args[1..]),
            "help" or "--help" or "-h" => PrintUsageAndExit(0),
            _ => UnknownCommand(args[0]),
        };
    }

    static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"imp: unknown command '{cmd}'. Run `imp help` for usage.");
        return 1;
    }

    static int PrintUsageAndExit(int code)
    {
        PrintUsage();
        return code;
    }

    static void PrintUsage()
    {
        Console.WriteLine("""
Usage: imp <command> [args]

Lifecycle:
  build <contract-path> [provider]   Run the executor against a contract.
                                     Long-running (minutes to tens of minutes).
                                     Emits proof-of-work JSON to stdout.
  research --mode=<m> "question"     Cheap-executor research run. --mode=fs reads the
    [--brief path]                   current checkout; --brief points at a structured
    [provider]                       brief markdown file. Emits report JSON to stdout
                                     and a sidecar archive to <repo>.researches/.
  wiki [path] [--dry-run]            Plan a per-directory wiki survey of the repo (or
                                     a subtree). --dry-run emits the SKIP/STUB/RUN plan
                                     and exits; without it, the orchestrator runs (not
                                     yet implemented).
  validate <contract-path>           Dry-run: parse + structural check, no model call.
  review <task-id>                   Bundled post-build view: proof-of-work + git diff.
                                     The canonical "what to do after a build" command.

Inspection:
  list                                List contracts under ./contracts/*.md (JSON).
  show <task-id>                     Print the contract markdown for a task.
  log <task-id>                      Print the rendered transcript of the most recent run.
  template <name>                    Print a template (contract | proof-of-work).

Diagnostics:
  ping [provider]                    Smoke-test a chat provider.
  ping-tools [provider]              Verify multi-turn tool-calling round-trips.
  render-transcript <trace-jsonl>    Re-render transcript.md from an existing trace.jsonl.

Operates on the current working directory. cwd must be a git repo for
build / validate / list / show / log / review.
""");
    }

    // --- shared config / chat construction ---

    static IConfiguration BuildConfiguration(string? providerOverride = null)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile("appsettings.json", optional: true);

        if (!string.IsNullOrEmpty(providerOverride))
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ActiveProvider"] = providerOverride,
            });
        }

        return builder.Build();
    }

    // --- core lifecycle subcommands ---

    static async Task<int> RunBuild(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp build <contract-path> [provider]");
            return 1;
        }
        var contractPath = args[0];
        var providerOverride = args.Length > 1 ? args[1] : null;
        var config = BuildConfiguration(providerOverride);
        var chat = Providers.Create(config);

        Console.Error.WriteLine($"[imp] build start: contractPath={contractPath} provider={config["ActiveProvider"]} cwd={Directory.GetCurrentDirectory()}");
        var json = await McpTools.Build(chat, config, contractPath);
        Console.WriteLine(json);
        return 0;
    }

    static async Task<int> RunResearch(string[] args)
    {
        // Accepted forms:
        //   imp research --mode=fs "question"
        //   imp research --mode=fs --brief contracts/R-007.md
        //   imp research --mode=fs --brief contracts/R-007.md gpt-mini
        // Trailing positional arg, if not consumed by --brief, is the question
        // (free-text). A trailing arg AFTER all flags is treated as a provider
        // override only if --brief was used and it doesn't look like a path
        // — keep it simple: provider override always comes via env / config
        // for v1, not as a positional.
        string? mode = null;
        string? briefPath = null;
        string? question = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--mode=", StringComparison.Ordinal))
                mode = a["--mode=".Length..];
            else if (a == "--mode" && i + 1 < args.Length)
                mode = args[++i];
            else if (a.StartsWith("--brief=", StringComparison.Ordinal))
                briefPath = a["--brief=".Length..];
            else if (a == "--brief" && i + 1 < args.Length)
                briefPath = args[++i];
            else if (question is null)
                question = a;
            else
                question += " " + a;
        }

        if (string.IsNullOrEmpty(mode))
        {
            Console.Error.WriteLine("Usage: imp research --mode=<name> \"question\" [--brief path]");
            Console.Error.WriteLine($"       known modes: {string.Join(", ", Modes.KnownNames())}");
            return 1;
        }
        if (string.IsNullOrEmpty(briefPath) && string.IsNullOrEmpty(question))
        {
            Console.Error.WriteLine("Usage: imp research --mode=<name> \"question\" [--brief path]");
            Console.Error.WriteLine("       supply either a free-text question or --brief <path>");
            return 1;
        }

        var config = BuildConfiguration();
        var chat = Providers.Create(config);

        Console.Error.WriteLine($"[imp] research start: mode={mode} brief={briefPath ?? "<free-text>"} provider={config["ActiveProvider"]} cwd={Directory.GetCurrentDirectory()}");
        var json = await Research.RunAsync(chat, config, mode, question, briefPath);
        Console.WriteLine(json);
        return 0;
    }

    static async Task<int> RunWiki(string[] args)
    {
        // Accepted forms:
        //   imp wiki                  — plan whole repo, dispatch
        //   imp wiki src/Foo          — plan subtree, dispatch
        //   imp wiki --dry-run [path] — plan only, print as JSON
        //   imp wiki --resume W-NNN   — resume an interrupted run from its manifest
        string? targetPath = null;
        bool dryRun = false;
        string? resumeId = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--dry-run") dryRun = true;
            else if (a.StartsWith("--resume=", StringComparison.Ordinal))
                resumeId = a["--resume=".Length..];
            else if (a == "--resume" && i + 1 < args.Length)
                resumeId = args[++i];
            else if (a.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"imp wiki: unknown flag '{a}'");
                return 1;
            }
            else if (targetPath is null) targetPath = a;
            else
            {
                Console.Error.WriteLine($"imp wiki: unexpected extra argument '{a}'");
                return 1;
            }
        }

        var cwd = Directory.GetCurrentDirectory();
        var repoRoot = Path.GetFullPath(cwd);
        if (!Directory.Exists(Path.Combine(repoRoot, ".git")) && !File.Exists(Path.Combine(repoRoot, ".git")))
        {
            Console.Error.WriteLine($"imp wiki: cwd is not a git repository: {repoRoot}");
            return 1;
        }

        var config = BuildConfiguration();

        // Resume path: load manifest from <repo>.wikis/W-NNN-<slug>/, ignore
        // any positional path argument, dispatch unfinished targets.
        if (!string.IsNullOrEmpty(resumeId))
        {
            if (!string.IsNullOrEmpty(targetPath) || dryRun)
            {
                Console.Error.WriteLine("imp wiki: --resume cannot be combined with a target path or --dry-run");
                return 1;
            }
            var existing = WikiArchive.FindByWikiId(repoRoot, resumeId);
            if (existing is null)
            {
                Console.Error.WriteLine($"imp wiki: no archive found for {resumeId} under {WikiArchive.RootFor(repoRoot)}");
                return 1;
            }
            var manifest = WikiArchive.ReadManifest(existing);
            if (manifest is null)
            {
                Console.Error.WriteLine($"imp wiki: manifest.json missing or unreadable in {existing}");
                return 1;
            }

            var chat = Providers.Create(config);
            Console.Error.WriteLine($"[imp] wiki resume: wikiId={manifest.WikiId} archive={existing} targets={manifest.Targets.Count}");
            var json = await Wiki.RunAsync(chat, config, manifest, existing);
            Console.WriteLine(json);
            return 0;
        }

        var wikiDir = config["Wiki:Dir"] ?? "wiki";
        var maxDirBytes = long.TryParse(config["Wiki:MaxDirBytes"], out var mdb) && mdb > 0 ? mdb : 40960L;
        var toolBudget = int.TryParse(config["Wiki:ToolBudget"], out var tb) && tb > 0 ? tb : Wiki.DefaultToolBudget;

        string targetSubpath = "";
        if (!string.IsNullOrEmpty(targetPath))
        {
            var abs = Path.GetFullPath(targetPath);
            if (!abs.Equals(repoRoot, StringComparison.Ordinal)
                && !abs.StartsWith(repoRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"imp wiki: target {abs} is outside repo {repoRoot}");
                return 1;
            }
            targetSubpath = Path.GetRelativePath(repoRoot, abs).Replace('\\', '/');
            if (targetSubpath == ".") targetSubpath = "";
        }

        var plan = WikiPlanner.Plan(repoRoot, targetSubpath, wikiDir, maxDirBytes);

        if (dryRun)
        {
            Console.WriteLine(WikiPlanner.SerializePlan(plan));
            return 0;
        }

        // Construct manifest from plan, allocate archive dir, dispatch.
        var slug = SlugForRun(targetSubpath);
        var freshManifest = Wiki.ManifestFromPlan(plan, targetSubpath, toolBudget, slug);
        var archiveDir = WikiArchive.DirectoryFor(repoRoot, freshManifest.WikiId, slug);

        var chatClient = Providers.Create(config);
        Console.Error.WriteLine($"[imp] wiki start: wikiId={freshManifest.WikiId} archive={archiveDir} targets={freshManifest.Targets.Count} provider={config["ActiveProvider"]}");
        var resultJson = await Wiki.RunAsync(chatClient, config, freshManifest, archiveDir);
        Console.WriteLine(resultJson);
        return 0;
    }

    static string SlugForRun(string targetSubpath)
        => string.IsNullOrEmpty(targetSubpath) ? "root" : BriefParser.SlugFrom(targetSubpath);

    static async Task<int> RunValidate(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp validate <contract-path>");
            return 1;
        }
        var json = await McpTools.ValidateContract(args[0]);
        Console.WriteLine(json);
        return 0;
    }

    static int RunReview(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp review <task-id>");
            return 1;
        }
        var bundle = McpTools.Review(args[0]);
        Console.Write(bundle);
        if (!bundle.EndsWith('\n')) Console.WriteLine();
        return 0;
    }

    // --- inspection subcommands ---

    static int RunList()
    {
        Console.WriteLine(McpTools.ListTasks());
        return 0;
    }

    static int RunShow(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp show <task-id>");
            return 1;
        }
        Console.Write(McpTools.GetContract(args[0]));
        return 0;
    }

    static int RunLog(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp log <task-id>");
            return 1;
        }
        Console.Write(McpTools.GetLog(args[0]));
        return 0;
    }

    static int RunTemplate(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp template <contract|proof-of-work>");
            return 1;
        }
        var fileName = args[0] switch
        {
            "contract" => "contract.md",
            "proof-of-work" => "proof-of-work.json",
            _ => null,
        };
        if (fileName is null)
        {
            Console.Error.WriteLine($"imp: unknown template '{args[0]}'. Available: contract, proof-of-work");
            return 1;
        }
        var path = Path.Combine(AppContext.BaseDirectory, "Templates", fileName);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"imp: template file not found at {path}");
            return 2;
        }
        Console.Write(File.ReadAllText(path));
        return 0;
    }

    // --- diagnostics ---

    static async Task<int> RunPing(string[] args)
    {
        var providerOverride = args.Length > 0 ? args[0] : null;
        var config = BuildConfiguration(providerOverride);
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
        return 0;
    }

    // Verifies multi-turn tool-calling round-trips correctly against the active
    // provider. Specifically checks that reasoning-model responses (TextReasoningContent)
    // don't break the follow-up turn after a tool call. If this works, the inner
    // executor loop is safe to build on the same pattern.
    static async Task<int> RunPingTools(string[] args)
    {
        var providerOverride = args.Length > 0 ? args[0] : null;
        var config = BuildConfiguration(providerOverride);
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
            return 1;
        }

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
        return 0;
    }

    // Regenerates transcript.md from an existing trace.jsonl. Useful for old
    // traces or for iterating on the renderer without re-running a contract.
    static int RunRenderTranscript(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp render-transcript <trace-jsonl-path>");
            return 1;
        }
        var tracePath = args[0];
        var outputPath = Path.Combine(
            Path.GetDirectoryName(tracePath) ?? ".",
            "transcript.md");
        TranscriptRenderer.Render(tracePath, outputPath);
        Console.Error.WriteLine($"[render-transcript] wrote {outputPath}");
        return 0;
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
}
