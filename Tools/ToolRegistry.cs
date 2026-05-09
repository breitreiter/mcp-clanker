using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Imp.Research;
using Imp.Infrastructure;

namespace Imp.Tools;

// The pluggability surface for research mode. Tools register here once with
// metadata; modes compose registered tools by name and the registry verifies
// each tool's Reach falls inside the mode's AllowedReach at resolution time.
//
// Build-mode code (Toolbox.Create) does NOT go through the registry — it
// constructs its tools directly. The registry is additive scaffolding for
// research mode and any future modes that follow.

// What environment-touching surface a tool opens. The audit knob:
// `web` mode declares `AllowedReach = { Network }`, `fs` mode declares
// `{ LocalFsRead }`, and a tool whose Reach isn't in the set can't be
// composed into that mode. Pure functions (e.g. `extract_text`) live at
// `None`; canary tools (post-v1) also live at `None` and fire a
// SafetyBreach when invoked instead of performing real work.
public enum ToolReach
{
    None,
    LocalFsRead,
    LocalFsWrite,
    Network,
    Subprocess,
}

// Closed over by IToolDefinition.Build to wire concrete tools to a working
// directory + sandbox profile + ambient config. Kept narrow on purpose — we
// add fields when a tool needs them, not before.
public sealed record ToolContext(
    string WorkingDirectory,
    SandboxConfig Sandbox,
    IConfiguration Config);

public interface IToolDefinition
{
    string Name { get; }
    ToolReach Reach { get; }
    AITool Build(ToolContext ctx);
}

public static class ToolRegistry
{
    static readonly Dictionary<string, IToolDefinition> _byName = new(StringComparer.Ordinal);
    static readonly object _lock = new();
    static bool _initialized;

    static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            Register(new ReadFileToolDefinition());
            Register(new GrepToolDefinition());
            Register(new ListDirToolDefinition());
            _initialized = true;
        }
    }

    public static void Register(IToolDefinition tool)
    {
        if (_byName.ContainsKey(tool.Name))
            throw new InvalidOperationException($"Tool '{tool.Name}' is already registered.");
        _byName[tool.Name] = tool;
    }

    public static IToolDefinition Get(string name)
    {
        EnsureInitialized();
        if (!_byName.TryGetValue(name, out var def))
            throw new InvalidOperationException($"No tool registered with name '{name}'.");
        return def;
    }

    // Resolve a mode's tool list against the registry. Three failure modes:
    //   1. unknown name (typo in the mode definition)
    //   2. reach mismatch (mode lists a tool whose Reach isn't in AllowedReach)
    //   3. unknown name appears valid but Build throws (caller's problem)
    // Each fails fast at startup with a specific message — no mid-run surprises.
    public static IReadOnlyList<AITool> ResolveForMode(ModeDefinition mode, ToolContext ctx)
    {
        EnsureInitialized();
        var result = new List<AITool>();
        foreach (var name in mode.ToolNames)
        {
            if (!_byName.TryGetValue(name, out var def))
                throw new InvalidOperationException(
                    $"Mode '{mode.Name}' references unknown tool '{name}'.");
            if (!mode.AllowedReach.Contains(def.Reach))
                throw new InvalidOperationException(
                    $"Mode '{mode.Name}' lists tool '{name}' (reach={def.Reach}) but AllowedReach is {{{string.Join(", ", mode.AllowedReach)}}}.");
            result.Add(def.Build(ctx));
        }
        return result;
    }
}
