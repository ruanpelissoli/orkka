namespace Orkka.Modules.Projects.Contracts;


/// <summary>
/// Decides which worker image a repository needs. The repo's own <c>orkka.json</c>
/// (<see cref="OrkkaManifest"/>) wins when it declares a <c>stack</c> — a repo that says
/// what it is beats any guess — and otherwise the manifest files at its root (and one level
/// down, for monorepos and `src/`-style layouts) decide. Pure file presence — no model, no
/// network — so it can run the moment a worktree exists and costs nothing.
///
/// Precedence matters where a repo carries several manifests: a Next.js app with a
/// <c>Dockerfile</c> and a <c>Makefile</c> is still Node; a Kotlin service with a
/// <c>package.json</c> for its lint tooling is still JVM. Package.json alone therefore wins
/// only when no heavier toolchain is present, and React Native / Expo wins over plain Node
/// because its image also carries the Android SDK. C/C++ sits in two places: a build
/// system of its own (CMake, Meson, autotools, Conan, vcpkg) outranks Node and Python,
/// whose C extensions still live in a package.json / pyproject project; bare sources with
/// at most a Makefile are the weakest signal and win only when nothing else matched.
/// </summary>
public static class RepoStackClassifier
{
    /// <summary>Returns a <see cref="RepoStacks"/> value; <see cref="RepoStacks.General"/>
    /// when nothing recognisable is found. Never throws — an unreadable tree is "general".</summary>
    public static string Classify(string rootPath)
    {
        try
        {
            if (!Directory.Exists(rootPath))
                return RepoStacks.General;

            if (OrkkaManifest.TryReadStackFromRepo(rootPath) is { } declared)
                return declared;

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Collect(rootPath, files, dirs, depth: 0);

            bool Has(params string[] names) => names.Any(files.Contains);
            bool HasExt(string ext) => files.Any(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            bool HasCSources() => CSourceExtensions.Any(HasExt);

            // Heavier toolchains first — see the type comment for why.
            if (Has("go.mod")) return RepoStacks.Go;
            if (Has("Cargo.toml")) return RepoStacks.Rust;
            if (Has("pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"))
                return RepoStacks.Jvm;
            if (HasExt(".sln") || HasExt(".csproj") || HasExt(".fsproj") || Has("global.json", "Directory.Build.props"))
                return RepoStacks.Dotnet;
            if (Has("composer.json")) return RepoStacks.Php;
            if (Has("Gemfile")) return RepoStacks.Ruby;
            if (HasExt(".rockspec") || Has("main.lua", "init.lua", "rockspec")) return RepoStacks.Lua;

            // React Native / Expo / native Android before plain Node: same package.json, bigger image.
            if (Has("app.json", "app.config.js", "app.config.ts", "eas.json") && Has("package.json")
                || dirs.Contains("android") && (Has("package.json") || Has("build.gradle"))
                || Has("pubspec.yaml"))
                return RepoStacks.Mobile;

            // After mobile (an Android tree carries CMakeLists.txt for its native modules), before
            // Node/Python: a repo that owns a C/C++ build system is a C/C++ repo even when it
            // has a package.json for docs tooling or a pyproject for bindings — and the cpp
            // image carries python3 + Node for exactly those.
            if (Has("CMakeLists.txt", "meson.build", "configure.ac", "conanfile.txt", "conanfile.py",
                    "vcpkg.json", "SConstruct", "xmake.lua", "premake5.lua", "BUILD.bazel", "WORKSPACE.bazel")
                && HasCSources())
                return RepoStacks.Cpp;

            if (Has("package.json", "pnpm-workspace.yaml", "bun.lockb", "deno.json", "deno.jsonc"))
                return RepoStacks.Node;

            if (Has("pyproject.toml", "requirements.txt", "Pipfile", "setup.py", "setup.cfg", "poetry.lock", "environment.yml"))
                return RepoStacks.Python;

            // Hand-rolled Makefile projects and single-file exercises: sources and nothing else.
            if (HasCSources())
                return RepoStacks.Cpp;

            return RepoStacks.General;
        }
        catch (Exception)
        {
            return RepoStacks.General;
        }
    }

    private static readonly string[] CSourceExtensions =
        [".c", ".cc", ".cpp", ".cxx", ".c++", ".h", ".hh", ".hpp", ".hxx"];

    /// <summary>Root plus two levels of sub-directories (skipping dependency/VCS folders):
    /// enough for <c>apps/web/package.json</c> or <c>src/Api/Api.csproj</c>, cheap on huge trees.</summary>
    private static void Collect(string dir, HashSet<string> files, HashSet<string> dirs, int depth)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            files.Add(Path.GetFileName(file));

        if (depth >= 2)
            return;

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.') || name is "node_modules" or "vendor" or "bin" or "obj" or "dist" or "build" or "target" or "__pycache__")
                continue;
            dirs.Add(name);
            Collect(sub, files, dirs, depth + 1);
        }
    }
}
