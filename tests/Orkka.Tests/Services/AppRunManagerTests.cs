namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner.Apps;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Terminal;

/// <summary>
/// The Run button's ordering and path rules. Both are pure and both have a failure mode worth
/// pinning: the wrong order starts a web app before the API it calls, and a working directory
/// that escapes the repo spawns a process somewhere the user never pointed at.
/// </summary>
public sealed class AppRunOrderingTests
{
    private static AppRunTargetSpec Target(string key, params string[] dependsOn) =>
        new($"run-{key}", key, key, "runner-userrepo:org:repo", null, "echo hi", null, dependsOn);

    [Fact]
    public void OrderByDependencies_StartsDependenciesFirst()
    {
        var ordered = AppRunManager.OrderByDependencies([Target("web", "api"), Target("api")]);

        ordered.Select(t => t.Key).Should().Equal("api", "web");
    }

    [Fact]
    public void OrderByDependencies_KeepsEveryTarget()
    {
        var ordered = AppRunManager.OrderByDependencies(
            [Target("web", "api"), Target("api", "db"), Target("db"), Target("docs")]);

        ordered.Should().HaveCount(4);
        ordered.Select(t => t.Key).Should().Equal("db", "api", "web", "docs");
    }

    [Fact]
    public void OrderByDependencies_SurvivesACycle()
    {
        // A hand-edited config can describe a cycle. Starting in some order beats hanging.
        var ordered = AppRunManager.OrderByDependencies([Target("a", "b"), Target("b", "a")]);

        ordered.Should().HaveCount(2);
        ordered.Select(t => t.Key).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void OrderByDependencies_IgnoresUnknownDependencies()
    {
        // A dependency on a target the user did not select is not a reason to drop this one.
        var ordered = AppRunManager.OrderByDependencies([Target("web", "api-not-selected")]);

        ordered.Select(t => t.Key).Should().Equal("web");
    }
}

public sealed class AppRunWorkingDirectoryTests
{
    [Fact]
    public void ResolveWorkingDirectory_NullMeansRepoRoot()
    {
        var root = Directory.GetCurrentDirectory();

        AppRunManager.ResolveWorkingDirectory(root, null)
            .Should().Be(Path.GetFullPath(root));
        AppRunManager.ResolveWorkingDirectory(root, "  ")
            .Should().Be(Path.GetFullPath(root));
        AppRunManager.ResolveWorkingDirectory(root, ".")
            .Should().Be(Path.GetFullPath(root));
    }

    [Fact]
    public void ResolveWorkingDirectory_ResolvesASubfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"orkka-run-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "apps", "web");
        Directory.CreateDirectory(nested);
        try
        {
            AppRunManager.ResolveWorkingDirectory(root, "apps/web")
                .Should().Be(Path.GetFullPath(nested));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../../elsewhere")]
    [InlineData("apps/../../elsewhere")]
    public void ResolveWorkingDirectory_RefusesToEscapeTheRepo(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), $"orkka-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            AppRunManager.ResolveWorkingDirectory(root, relative).Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveWorkingDirectory_ReturnsNullWhenTheFolderIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"orkka-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            AppRunManager.ResolveWorkingDirectory(root, "apps/web").Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class RunCommandShellTests
{
    [Fact]
    public void ResolveForCommand_PassesTheCommandToAnExistingShell()
    {
        var shell = ShellResolver.ResolveForCommand("npm run dev");

        File.Exists(shell.Path).Should().BeTrue();
        shell.Args.Should().Contain("npm run dev",
            "the command must reach the shell as one argument, not be split on spaces");
    }

    [Fact]
    public void ResolveForCommand_UsesTheShellsRunOneCommandSwitch()
    {
        var shell = ShellResolver.ResolveForCommand("echo hi");

        var expected = shell.Name switch
        {
            "pwsh" => "-Command",
            "cmd" => "/c",
            _ => "-lc",
        };
        shell.Args.Should().Contain(expected);
    }

    [Fact]
    public void ResolveForCommand_NeverPicksWindowsPowerShellForARunCommand()
    {
        // Windows PowerShell 5.1 has no `&&` and refuses npm.ps1 under the default execution
        // policy — the two most common ways a detected command dies on Windows.
        var shell = ShellResolver.ResolveForCommand("cd apps/web && npm run dev");

        shell.Name.Should().NotBe("powershell");
        if (OperatingSystem.IsWindows())
        {
            shell.Name.Should().BeOneOf("pwsh", "cmd");
            if (shell.Name == "pwsh")
                shell.Args.Should().Equal("-NoLogo", "-NoProfile", "-Command", "cd apps/web && npm run dev");
        }
    }

    [Fact]
    public void ResolveForCommand_NeverBypassesTheExecutionPolicy()
    {
        var shell = ShellResolver.ResolveForCommand("npm run dev");

        shell.Args.Should().NotContain(a => a.Equals("-ExecutionPolicy", StringComparison.OrdinalIgnoreCase)
            || a.Equals("Bypass", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("npm : File C:\\nodejs\\npm.ps1 cannot be loaded because running scripts is disabled on this system.", true)]
    [InlineData("File .\\build.ps1 is not digitally signed. You cannot run this script on the current system.", true)]
    [InlineData("Error: Cannot find module 'next'", false)]
    public void An_execution_policy_refusal_is_explained_never_bypassed(string output, bool explained)
    {
        var hint = ShellResolver.ExecutionPolicyHint(output);

        if (explained)
            hint.Should().Contain("does not bypass");
        else
            hint.Should().BeNull();
    }
}

public sealed class AppRunPreparerTests
{
    private static AppRunTargetSpec Target(string command, string? install = null, string? kind = null) =>
        new("run-web", "web", "Web", "runner-userrepo:org:repo", null, command, 3000, [],
            InstallCommand: install, Kind: kind);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"orkka-prep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ProvisionEnvFile_CopiesTheExampleWhenNoEnvExists()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env.example"), "DATABASE_URL=postgres://localhost/app\n");

            var note = AppRunPreparer.ProvisionEnvFile(dir);

            note.Should().Contain(".env.example");
            File.ReadAllText(Path.Combine(dir, ".env")).Should().Contain("DATABASE_URL=");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProvisionEnvFile_NeverTouchesAnExistingEnv()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env.example"), "A=example\n");
            File.WriteAllText(Path.Combine(dir, ".env"), "A=mine\n");

            AppRunPreparer.ProvisionEnvFile(dir).Should().BeNull();
            File.ReadAllText(Path.Combine(dir, ".env")).Should().Be("A=mine\n");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProvisionEnvFile_DoesNothingWithoutAnExample()
    {
        var dir = TempDir();
        try
        {
            AppRunPreparer.ProvisionEnvFile(dir).Should().BeNull();
            File.Exists(Path.Combine(dir, ".env")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ShouldInstall_WhenAJsAppHasNoNodeModules()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), "{}");

            AppRunPreparer.ShouldInstall(dir, Target("npm run dev", "npm install")).Should().BeTrue();

            Directory.CreateDirectory(Path.Combine(dir, "node_modules"));
            AppRunPreparer.ShouldInstall(dir, Target("npm run dev", "npm install")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A `node_modules` that exists proves nothing. In a folder Orkka merges into, a
    /// task adds a package, the merge lands it, nobody installs it there, and Run dies with
    /// "'next' is not recognized" (prod, 2026-09-16). Every package the manifest declares
    /// has to be present — scoped ones under their scope folder.</summary>
    [Fact]
    public void ShouldInstall_WhenADeclaredDependencyIsNotInNodeModules()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"),
                "{\"dependencies\":{\"next\":\"15.0.0\",\"@scope/ui\":\"1.0.0\"},\"devDependencies\":{\"typescript\":\"5\"}}");
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "typescript"));
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "@scope", "ui"));

            AppRunPreparer.FindInstallReason(dir, Target("npm run dev", "npm install"))
                .Should().Be("'next' from package.json is not installed");

            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "next"));
            AppRunPreparer.ShouldInstall(dir, Target("npm run dev", "npm install")).Should().BeFalse();

            Directory.Delete(Path.Combine(dir, "node_modules", "@scope", "ui"), recursive: true);
            AppRunPreparer.FindInstallReason(dir, Target("npm run dev", "npm install"))
                .Should().Be("'@scope/ui' from package.json is not installed");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A lockfile written on another OS has no entry for this machine's native build
    /// of next's SWC (or tailwind's oxide, lightningcss, esbuild …), so `npm install` here
    /// leaves it out and next dev dies with "Turbopack is not supported on this platform
    /// (win32/x64) because native bindings are not available" (prod, 2026-09-16). The step
    /// adds the missing build with `--no-save`, so the user's lockfile stays untouched.</summary>
    [Fact]
    public void Prepare_InstallsTheNativeBuildForThisMachine_WhenTheLockfileLeftItOut()
    {
        var token = AppRunPreparer.PlatformToken();
        token.Should().NotBeNull();
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), "{\"dependencies\":{\"next\":\"16.0.0\"}}");
            var next = Path.Combine(dir, "node_modules", "next");
            Directory.CreateDirectory(next);
            File.WriteAllText(Path.Combine(next, "package.json"),
                "{\"name\":\"next\",\"optionalDependencies\":{\"@next/swc-" + token + "-msvc\":\"16.0.0\",\"@next/swc-other-arch\":\"16.0.0\"}}");

            var prepared = AppRunPreparer.Prepare(dir, Target("npm run dev", "npm install"));

            prepared.InstallCommand.Should().Be($"npm install --no-save @next/swc-{token}-msvc@16.0.0");
            prepared.Notes.Should().ContainSingle().Which.Should().Contain("native build").And.Contain(token!);

            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "@next", $"swc-{token}-msvc"));
            AppRunPreparer.Prepare(dir, Target("npm run dev", "npm install")).InstallCommand.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Prepare_OnlyAdvises_WhenTheNativeBuildIsMissingUnderAnotherPackageManager()
    {
        var token = AppRunPreparer.PlatformToken()!;
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), "{\"dependencies\":{\"next\":\"16.0.0\"}}");
            var next = Path.Combine(dir, "node_modules", "next");
            Directory.CreateDirectory(next);
            File.WriteAllText(Path.Combine(next, "package.json"),
                "{\"optionalDependencies\":{\"@next/swc-" + token + "-msvc\":\"16.0.0\"}}");

            var prepared = AppRunPreparer.Prepare(dir, Target("pnpm dev", "pnpm install"));

            prepared.InstallCommand.Should().BeNull("pnpm's flags differ and a wrong guess rewrites the lockfile");
            prepared.Notes.Should().ContainSingle().Which.Should().Contain("delete node_modules and the lockfile");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ShouldInstall_CountsAHoistedDependencyAsPresent()
    {
        var root = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "package.json"), "{}");
            File.WriteAllText(Path.Combine(root, "package-lock.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "next"));
            var web = Path.Combine(root, "apps", "web");
            Directory.CreateDirectory(web);
            File.WriteAllText(Path.Combine(web, "package.json"), "{\"dependencies\":{\"next\":\"15\",\"react\":\"19\"}}");
            Directory.CreateDirectory(Path.Combine(web, "node_modules", "react"));

            AppRunPreparer.ShouldInstall(web, Target("npm run dev", "npm install")).Should().BeFalse(
                "next is hoisted to the workspace root and react is local");

            Directory.Delete(Path.Combine(root, "node_modules", "next"), recursive: true);
            AppRunPreparer.ShouldInstall(web, Target("npm run dev", "npm install")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ShouldInstall_TreatsAnUnreadableManifestAsInstalled()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), "{ not json");
            Directory.CreateDirectory(Path.Combine(dir, "node_modules"));
            AppRunPreparer.ShouldInstall(dir, Target("npm run dev", "npm install")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ShouldInstall_RespectsHoistedWorkspaceModules()
    {
        var root = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "package.json"), "{}");
            File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages: ['apps/*']");
            Directory.CreateDirectory(Path.Combine(root, "node_modules"));
            var web = Path.Combine(root, "apps", "web");
            Directory.CreateDirectory(web);
            File.WriteAllText(Path.Combine(web, "package.json"), "{}");

            AppRunPreparer.ShouldInstall(web, Target("pnpm dev", "pnpm install")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ShouldInstall_IsFalseWithoutAnInstallCommandOrWhenTheCommandInstallsItself()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), "{}");

            AppRunPreparer.ShouldInstall(dir, Target("npm run dev")).Should().BeFalse();
            AppRunPreparer.ShouldInstall(dir, Target("npm install && npm run dev", "npm install"))
                .Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ShouldInstall_LeavesNonJsProjectsAlone()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "go.mod"), "module app");
            AppRunPreparer.ShouldInstall(dir, Target("go run .", "go mod download")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("docker compose up postgres", null, true)]
    [InlineData("docker-compose up", null, true)]
    [InlineData("npm run dev", "infra", true)]
    [InlineData("npm run dev", "web", false)]
    [InlineData("./scripts/dockerize.sh", null, false)]
    public void NeedsDocker_OnlyForInfraOrDockerCommands(string command, string? kind, bool expected)
    {
        AppRunManager.NeedsDocker(Target(command, kind: kind)).Should().Be(expected);
    }
}
