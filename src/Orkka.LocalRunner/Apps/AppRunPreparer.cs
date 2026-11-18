namespace Orkka.LocalRunner.Apps;

using Orkka.LocalRunner.Contracts;

/// <summary>What the prepare step decided for one target, and what it wants said about it.</summary>
/// <param name="InstallCommand">A command to run ahead of the target's own, in the same shell
/// (`install &amp;&amp; command`): the target's declared install, or the narrower one this
/// step chose. Null when nothing has to run first.</param>
/// <param name="Notes">Lines to show in the target's log before the process's own output.</param>
internal sealed record PreparedRun(string? InstallCommand, IReadOnlyList<string> Notes)
{
    public bool InstallFirst => InstallCommand is not null;
}

/// <summary>
/// Gets a target's folder into a state where its command can actually succeed — the
/// difference between a Run button that works on a fresh clone and one that fails with
/// "Cannot find module" or "DATABASE_URL is not set" and blames the user.
///
/// Two things, both conservative:
/// <list type="bullet">
/// <item><b>.env from .env.example.</b> Only when there is no `.env` at all and the repo ships
/// an example. The example holds local defaults the project's own author wrote for exactly
/// this situation — never secrets, which is why copying it is safe and why an existing `.env`
/// is never touched.</item>
/// <item><b>Install when dependencies are evidently missing.</b> A JS app with no
/// `node_modules` next to its manifest cannot start; running its declared install command
/// first is what a person would do. Anything less clear-cut (a Gradle cache, a virtualenv)
/// stays manual — guessing wrong there means minutes of downloads the user did not ask for.</item>
/// </list>
/// The command itself is never invented here: the install command comes from the stored
/// target, and the runner only decides whether this is the moment to run it.
/// </summary>
internal static class AppRunPreparer
{
    private static readonly string[] EnvExampleNames =
        [".env.example", ".env.local.example", ".env.sample", ".env.template"];

    public static PreparedRun Prepare(string cwd, AppRunTargetSpec target)
    {
        var notes = new List<string>();

        if (ProvisionEnvFile(cwd) is { } created)
            notes.Add(created);

        var installReason = FindInstallReason(cwd, target);
        if (installReason is not null)
        {
            notes.Add($"{installReason} — running: {target.InstallCommand}");
            return new PreparedRun(target.InstallCommand, notes);
        }

        // Everything declared is installed — but is the native build for THIS machine?
        var nativeInstall = FindNativeBuildInstall(cwd, target, out var nativeNote);
        if (nativeNote is not null)
            notes.Add(nativeNote);
        return new PreparedRun(nativeInstall, notes);
    }

    /// <summary>
    /// The install that puts a dependency's native build for this platform in place, or null.
    /// Packages with native code (next's SWC, tailwind's oxide, lightningcss, esbuild, rollup)
    /// ship one optional dependency per platform, and npm installs only the current platform's
    /// — as recorded in the lockfile. A lockfile written on another OS (an agent's Linux pod,
    /// a teammate's Mac) has no entry for this one, and `npm install` here leaves it out; next
    /// dev then dies with "Turbopack is not supported on this platform (win32/x64) because
    /// native bindings are not available" (prod, 2026-09-16). `npm install --no-save` adds the
    /// missing build without touching package.json or the lockfile — the user's tree stays
    /// clean for the merges Orkka lands in it. Other package managers get the remedy as a
    /// note: their flags differ, and guessing one wrong rewrites the user's lockfile.
    /// </summary>
    internal static string? FindNativeBuildInstall(string cwd, AppRunTargetSpec target, out string? note)
    {
        note = null;
        if (string.IsNullOrWhiteSpace(target.InstallCommand) || !LooksLikeJsProject(cwd))
            return null;
        if (PlatformToken() is not { } token)
            return null;

        var moduleDirs = ModuleDirectories(cwd);
        if (moduleDirs.Count == 0)
            return null;

        var missing = MissingNativeBuilds(moduleDirs, token);
        if (missing.Count == 0)
            return null;

        var what = $"'{missing[0].Name}' — the native build of a dependency for this machine ({token}) — "
            + "is not installed; the lockfile was probably written on another OS.";
        if (!target.InstallCommand.TrimStart().StartsWith("npm ", StringComparison.OrdinalIgnoreCase))
        {
            note = $"{what} Reinstall on this machine: delete node_modules and the lockfile, then run {target.InstallCommand}.";
            return null;
        }

        var command = "npm install --no-save " + string.Join(" ", missing.Select(m => $"{m.Name}@{m.Version}"));
        note = $"{what} Running: {command}";
        return command;
    }

    /// <summary>`win32-x64`, `darwin-arm64`, `linux-x64` … as npm's native packages name
    /// platforms. Null on a platform no such package targets.</summary>
    internal static string? PlatformToken()
    {
        var os = OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsFreeBSD() ? "freebsd"
            : null;
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "ia32",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => null,
        };
        return os is null || arch is null ? null : $"{os}-{arch}";
    }

    /// <summary>For every installed package whose `optionalDependencies` name builds for this
    /// platform and none of them is present: those builds (name and declared version). On
    /// Linux a package may offer both a glibc and a musl build; both are returned and both
    /// install, which is harmless.</summary>
    internal static IReadOnlyList<(string Name, string Version)> MissingNativeBuilds(
        IReadOnlyList<string> moduleDirs, string token)
    {
        var missing = new List<(string Name, string Version)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modules in moduleDirs)
        {
            foreach (var manifest in PackageManifests(modules))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest), ManifestJson);
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                        || !doc.RootElement.TryGetProperty("optionalDependencies", out var optional)
                        || optional.ValueKind != System.Text.Json.JsonValueKind.Object)
                        continue;

                    var forThisPlatform = optional.EnumerateObject()
                        .Where(p => p.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                        .Select(p => (Name: p.Name,
                            Version: p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString()! : "latest"))
                        .ToList();
                    if (forThisPlatform.Count == 0)
                        continue;
                    if (forThisPlatform.Any(p => moduleDirs.Any(dir =>
                            Directory.Exists(Path.Combine(dir, p.Name.Replace('/', Path.DirectorySeparatorChar))))))
                        continue;

                    foreach (var p in forThisPlatform)
                        if (seen.Add(p.Name))
                            missing.Add(p);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    // One unreadable manifest is not a reason to skip the rest.
                }
            }
        }
        return missing;
    }

    private static readonly System.Text.Json.JsonDocumentOptions ManifestJson = new()
    {
        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Every installed package's manifest: `node_modules/x/package.json` and
    /// `node_modules/@scope/x/package.json`. Flat, so a thousand packages is a thousand
    /// small reads — well under a second, once per Run press.</summary>
    private static List<string> PackageManifests(string modules)
    {
        var result = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(modules))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.'))
                    continue;
                if (name.StartsWith('@'))
                {
                    foreach (var scoped in Directory.EnumerateDirectories(dir))
                        AddManifest(scoped);
                }
                else
                {
                    AddManifest(dir);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A vanishing or unreadable node_modules: nothing to inspect.
        }
        return result;

        void AddManifest(string dir)
        {
            var manifest = Path.Combine(dir, "package.json");
            if (File.Exists(manifest))
                result.Add(manifest);
        }
    }

    /// <summary>Copies the first example env file found to `.env` when no `.env` exists.
    /// Returns the note to show, or null when nothing was done.</summary>
    internal static string? ProvisionEnvFile(string cwd)
    {
        var envPath = Path.Combine(cwd, ".env");
        if (File.Exists(envPath))
            return null;

        foreach (var name in EnvExampleNames)
        {
            var example = Path.Combine(cwd, name);
            if (!File.Exists(example))
                continue;
            try
            {
                File.Copy(example, envPath);
                return $"Created .env from {name} — edit it if your local setup differs.";
            }
            catch (IOException)
            {
                // Racing another writer, or a read-only folder: the app will say what it is
                // missing, which is no worse than before this step existed.
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// True when the target declares an install command and its dependencies are evidently
    /// absent. Only the JS family is inspected — `node_modules` is an unambiguous signal, and
    /// it is also where the overwhelming majority of "Run failed" reports come from. The
    /// command's own text may already install (`npm install &amp;&amp; …`); then nothing is
    /// prepended.
    /// </summary>
    internal static bool ShouldInstall(string cwd, AppRunTargetSpec target)
        => FindInstallReason(cwd, target) is not null;

    /// <summary>
    /// Why the install command has to run first, or null when it does not. Two signals:
    /// no `node_modules` at all (a fresh clone), and a `node_modules` that lacks a package
    /// `package.json` declares. The second is the everyday case in a folder Orkka merges
    /// into: a task adds a dependency, the merge lands it in the user's working copy, nobody
    /// runs npm install there, and the next Run dies with "'next' is not recognized" or
    /// "Cannot find module" (prod, 2026-09-16). A folder that merely EXISTS proves nothing.
    /// </summary>
    internal static string? FindInstallReason(string cwd, AppRunTargetSpec target)
    {
        if (string.IsNullOrWhiteSpace(target.InstallCommand))
            return null;
        if (ContainsInstall(target.Command))
            return null;
        if (!LooksLikeJsProject(cwd))
            return null;

        var moduleDirs = ModuleDirectories(cwd);
        if (moduleDirs.Count == 0)
            return "Dependencies look missing";

        var missing = FirstMissingDependency(cwd, moduleDirs);
        return missing is null ? null : $"'{missing}' from package.json is not installed";
    }

    /// <summary>The `node_modules` folders a dependency of this package may live in: its own,
    /// and the workspace root's when it is a member of a hoisting monorepo.</summary>
    private static List<string> ModuleDirectories(string cwd)
    {
        var dirs = new List<string>();
        var own = Path.Combine(cwd, "node_modules");
        if (Directory.Exists(own))
            dirs.Add(own);
        if (WorkspaceRootModules(cwd) is { } hoisted)
            dirs.Add(hoisted);
        return dirs;
    }

    /// <summary>The first `dependencies`/`devDependencies` entry with no folder under any of
    /// <paramref name="moduleDirs"/>, or null when every declared package is present. An
    /// unreadable package.json counts as present: guessing wrong there means minutes of
    /// downloads the user did not ask for, and the app will say what it is missing.</summary>
    private static string? FirstMissingDependency(string cwd, IReadOnlyList<string> moduleDirs)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(cwd, "package.json")), ManifestJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;

            foreach (var section in new[] { "dependencies", "devDependencies" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var deps)
                    || deps.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                foreach (var dep in deps.EnumerateObject())
                {
                    var name = dep.Name;
                    if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal))
                        continue;
                    var relative = name.Replace('/', Path.DirectorySeparatorChar);
                    if (!moduleDirs.Any(dir => Directory.Exists(Path.Combine(dir, relative))))
                        return name;
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool ContainsInstall(string command)
        => command.Contains(" install", StringComparison.OrdinalIgnoreCase)
            || command.Contains("npm ci", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeJsProject(string cwd)
        => File.Exists(Path.Combine(cwd, "package.json"));

    /// <summary>A monorepo package's dependencies are hoisted to the workspace root — a
    /// missing `apps/web/node_modules` says nothing when `node_modules` exists two levels
    /// up alongside a lockfile. Returns that root `node_modules`, or null.</summary>
    private static string? WorkspaceRootModules(string cwd)
    {
        var dir = Directory.GetParent(cwd);
        for (var depth = 0; dir is not null && depth < 4; depth++, dir = dir.Parent)
        {
            var hasLock = File.Exists(Path.Combine(dir.FullName, "package-lock.json"))
                || File.Exists(Path.Combine(dir.FullName, "pnpm-lock.yaml"))
                || File.Exists(Path.Combine(dir.FullName, "yarn.lock"))
                || File.Exists(Path.Combine(dir.FullName, "bun.lockb"))
                || File.Exists(Path.Combine(dir.FullName, "pnpm-workspace.yaml"));
            if (hasLock)
            {
                var modules = Path.Combine(dir.FullName, "node_modules");
                return Directory.Exists(modules) ? modules : null;
            }
        }
        return null;
    }
}
