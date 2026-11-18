namespace Orkka.Modules.Projects.Contracts;

/// <summary>
/// The toolchain a repository needs, named after the worker image variants CD builds
/// (Dockerfile.worker's <c>worker-&lt;stack&gt;</c> targets). Cloud task routing uses it to
/// land a run on a worker pool whose image actually carries the compiler/runtime the repo
/// needs: every image has git + Node 22 + Claude/codex/gemini, but only the <c>jvm</c>
/// image has a JDK, only <c>go</c> has Go, and so on.
///
/// Stored on <c>ProjectRepo.Stack</c>. Repos are classified from their orkka.json `stack` or
/// their manifest files the first time a cloud worker has the worktree on disk, and stay <c>null</c>
/// (= general) until then.
/// </summary>
public static class RepoStacks
{
    public const string Node = "node";
    public const string Python = "python";
    public const string Go = "go";
    public const string Rust = "rust";
    public const string Jvm = "jvm";
    public const string Dotnet = "dotnet";
    public const string Php = "php";
    public const string Ruby = "ruby";
    public const string Lua = "lua";
    /// <summary>C and C++ together: one toolchain (gcc/clang, cmake, ninja, meson, make),
    /// one image. A plain C repo is <c>cpp</c> too.</summary>
    public const string Cpp = "cpp";
    public const string Mobile = "mobile";

    /// <summary>The default worker image — Node only, nothing beyond what every image carries.
    /// What an unclassified repo maps to; every named stack has its own pool, so this one
    /// never serves a toolchain by fallback.</summary>
    public const string General = "general";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Node, Python, Go, Rust, Jvm, Dotnet, Php, Ruby, Lua, Cpp, Mobile, General,
    };

    /// <summary>Lower-cased known stack, or <see cref="General"/> for null/unknown values —
    /// a typo in config or a stale value must never strand a task on a pool nobody serves.</summary>
    public static string Normalize(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack))
            return General;
        var trimmed = stack.Trim().ToLowerInvariant();
        return All.Contains(trimmed) ? trimmed : General;
    }
}
