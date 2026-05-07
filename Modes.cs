using Microsoft.Extensions.AI;

namespace Imp;

// Research-mode declarations. A mode is a (toolset, sandbox-profile,
// system-prompt, finish-tool) tuple — the toolset is the customization
// axis (consumers swap in their own to get OLAP / Lucene / graph modes
// without forking the loop), and the rest follows from it.
//
// SandboxProfile is the per-mode shape declaration ("I want network=none
// and mount=ro"). The runtime SandboxConfig (Host vs Docker, image, etc.)
// composes with it: in Docker mode the profile becomes structural; in
// Host mode it's documentary, since the structural guarantee already
// comes from the tool surface (a mode that lists no write tool can't
// write, regardless of sandbox).

public enum MountPolicy
{
    None,       // no repo bind-mount (web mode)
    ReadOnly,   // bind-mounted ro (fs mode)
    ReadWrite,  // bind-mounted rw (build mode, today)
}

public sealed record SandboxProfile(
    bool AllowNetwork,
    MountPolicy RepoMount,
    bool AllowSubprocess);

public sealed record ModeDefinition(
    string Name,
    SandboxProfile Sandbox,
    IReadOnlySet<ToolReach> AllowedReach,
    IReadOnlyList<string> ToolNames,
    string SystemPromptFileName,
    Func<ResearchState, AIFunction> FinishToolFactory);

public static class Modes
{
    static readonly Dictionary<string, ModeDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();
    static bool _initialized;

    static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            Register(BuildFsMode());
            _initialized = true;
        }
    }

    public static void Register(ModeDefinition mode)
    {
        if (_byName.ContainsKey(mode.Name))
            throw new InvalidOperationException($"Mode '{mode.Name}' is already registered.");
        _byName[mode.Name] = mode;
    }

    public static ModeDefinition Get(string name)
    {
        EnsureInitialized();
        if (!_byName.TryGetValue(name, out var mode))
            throw new InvalidOperationException(
                $"No mode registered with name '{name}'. Known modes: {string.Join(", ", _byName.Keys)}.");
        return mode;
    }

    public static IReadOnlyCollection<string> KnownNames()
    {
        EnsureInitialized();
        return _byName.Keys.ToList();
    }

    static ModeDefinition BuildFsMode() => new(
        Name: "fs",
        Sandbox: new SandboxProfile(
            AllowNetwork: false,
            RepoMount: MountPolicy.ReadOnly,
            AllowSubprocess: false),
        AllowedReach: new HashSet<ToolReach> { ToolReach.None, ToolReach.LocalFsRead },
        ToolNames: new[] { "read_file", "grep", "list_dir" },
        SystemPromptFileName: "research-fs.md",
        FinishToolFactory: ResearchTools.BuildFinishResearchTool);
}
