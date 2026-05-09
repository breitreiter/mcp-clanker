using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Imp.Tools;

// IToolDefinition wrappers for the three read-only file tools. Each Build()
// call constructs a fresh AIFunction closed over the supplied ToolContext —
// the AIFunction's name + description live here as the canonical source for
// the registry lookup. The existing direct factories in Tools.Create /
// Tools.CreateReadOnly continue to work and are unchanged; this file is
// additive, used by research mode.

public sealed class ReadFileToolDefinition : IToolDefinition
{
    public string Name => "read_file";
    public ToolReach Reach => ToolReach.LocalFsRead;

    public AITool Build(ToolContext ctx) =>
        AIFunctionFactory.Create(
            (
                [Description("Path relative to the working directory.")] string path,
                [Description("Optional 1-based line to start from. Defaults to the whole file.")] int? offset = null,
                [Description("Optional max number of lines to return.")] int? limit = null)
            => Toolbox.ReadFile(path, offset, limit, ctx.WorkingDirectory),
            name: Name,
            description: "Read the contents of a text file relative to the working directory. Supports pagination via offset/limit.");
}

public sealed class GrepToolDefinition : IToolDefinition
{
    public string Name => "grep";
    public ToolReach Reach => ToolReach.LocalFsRead;

    public AITool Build(ToolContext ctx) =>
        AIFunctionFactory.Create(
            (
                [Description("Regular expression to search for.")] string pattern,
                [Description("Directory or file to search, relative to the working directory. Empty or omitted = the whole working directory.")] string? path = null,
                [Description("Filename glob filter like `*.cs` or `*Test*.cs`. Applied to filename only; path-qualified globs aren't supported — narrow with `path=` instead.")] string? file_pattern = null,
                [Description("If true, case-insensitive search. Default false.")] bool? case_insensitive = null,
                [Description("Max results to return. Default 100.")] int? max_results = null,
                [Description("`content` (default) returns matching lines as `file:line: content`. `files_with_matches` returns only matching file paths.")] string? output_mode = null)
            => GrepTool.Grep(pattern, path, file_pattern, case_insensitive, max_results, output_mode, ctx.WorkingDirectory),
            name: Name,
            description: "Search file contents with a regex. Automatically skips binary files and common non-source directories (.git, node_modules, bin, obj, .vs, __pycache__, .venv, venv, .idea, dist, build, .next, .nuget). Returns `file:line: content` lines by default; set output_mode=files_with_matches for file paths only.");
}

public sealed class ListDirToolDefinition : IToolDefinition
{
    public string Name => "list_dir";
    public ToolReach Reach => ToolReach.LocalFsRead;

    public AITool Build(ToolContext ctx) =>
        AIFunctionFactory.Create(
            (
                [Description("Directory path relative to the working directory. Empty or omitted = the working directory itself.")] string? path = null)
            => Toolbox.ListDir(path, ctx.WorkingDirectory),
            name: Name,
            description: "List the contents of a directory. Returns `[dir] name` and `[file] name` entries, directories first, both alphabetically sorted. Skips the same non-source directories as grep.");
}
