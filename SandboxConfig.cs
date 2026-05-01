using Microsoft.Extensions.Configuration;

namespace Imp;

// Captures how the bash tool should execute commands — either directly on
// the host shell (fast, no isolation) or inside a Docker container with
// --network=none and a bind-mounted worktree (slower per call, but
// contained). Parsed once per build from IConfiguration.

public enum SandboxMode
{
    Host,    // host bash directly (Git Bash on Windows); what imp did pre-Phase 6.
    Docker,  // docker run per command; the v2-production configuration.
}

public sealed record SandboxConfig(
    SandboxMode Mode,
    string Image,
    string NugetVolume,
    string MemoryLimit,
    string CpuLimit,
    int PidsLimit,
    string Network)
{
    // Safe defaults: Host mode keeps backward compatibility with every
    // previous contract run. Flip Mode to Docker in appsettings.json to
    // opt into the sandbox; the other fields only matter in Docker mode.
    public static SandboxConfig Default { get; } = new(
        Mode: SandboxMode.Host,
        Image: "imp-sandbox:latest",
        NugetVolume: "imp-nuget",
        MemoryLimit: "2g",
        CpuLimit: "2",
        PidsLimit: 256,
        Network: "none");

    public static SandboxConfig FromConfiguration(IConfiguration config)
    {
        var section = config.GetSection("Sandbox");
        if (!section.Exists()) return Default;

        var modeRaw = section["Mode"];
        var mode = modeRaw?.Trim().ToLowerInvariant() switch
        {
            "docker" => SandboxMode.Docker,
            "host" or null or "" => SandboxMode.Host,
            _ => SandboxMode.Host,
        };

        return new SandboxConfig(
            Mode: mode,
            Image: section["Image"] ?? Default.Image,
            NugetVolume: section["NugetVolume"] ?? Default.NugetVolume,
            MemoryLimit: section["MemoryLimit"] ?? Default.MemoryLimit,
            CpuLimit: section["CpuLimit"] ?? Default.CpuLimit,
            PidsLimit: int.TryParse(section["PidsLimit"], out var p) ? p : Default.PidsLimit,
            Network: section["Network"] ?? Default.Network);
    }
}
