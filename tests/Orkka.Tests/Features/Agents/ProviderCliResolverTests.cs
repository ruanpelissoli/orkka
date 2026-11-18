namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.SharedKernel.Ai;

/// <summary>
/// The resolver is the single source of truth for "where is this CLI", shared by the readiness
/// probe and the spawn. A machine that reports a provider connected must be able to launch it.
/// </summary>
public sealed class ProviderCliResolverTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("orkka-cli-resolver").FullName;
    private readonly Dictionary<string, string?> _env = new(StringComparer.OrdinalIgnoreCase);

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    // Never touches the real process environment: blanking PATH host-wide broke every
    // sibling test that shells out to git.
    private void SetVar(string name, string? value)
    {
        _env[name] = value;
        ProviderCliResolver.ReadEnv = key => _env.GetValueOrDefault(key);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenTheCliIsNowhere()
    {
        SetVar("PATH", _dir);

        // Null is a real answer. Callers report the provider unavailable rather than
        // spawning a bare name and discovering the truth mid-task.
        ProviderCliResolver.Resolve("definitely-not-a-real-cli").Should().BeNull();
    }

    [Fact]
    public void Resolve_FindsTheCliOnPath()
    {
        var exe = Touch(OperatingSystem.IsWindows() ? "faketool.exe" : "faketool");
        SetVar("PATH", _dir);

        ProviderCliResolver.Resolve("faketool").Should().Be(exe);
    }

    [Fact]
    public void Resolve_HonorsTheOperatorOverride()
    {
        var exe = Touch("elsewhere.bin");
        SetVar("PATH", "");
        SetVar(ProviderCliResolver.EnvVarFor("faketool"), exe);

        ProviderCliResolver.Resolve("faketool").Should().Be(Path.GetFullPath(exe));
    }

    [Fact]
    public void Resolve_IgnoresAnOverridePointingAtNothing()
    {
        SetVar("PATH", "");
        SetVar(ProviderCliResolver.EnvVarFor("faketool"), Path.Combine(_dir, "missing.exe"));

        // Fails closed: a typo'd override must not fall through to a PATH search that could
        // land on a different binary of the same name.
        ProviderCliResolver.Resolve("faketool").Should().BeNull();
    }

    [Fact]
    public void Resolve_PrefersTheNativeExeOverAScriptShim()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Touch("shimmed.cmd");
        var exe = Touch("shimmed.exe");
        SetVar("PATH", _dir);

        // The SDK spawns with piped stdio, which a .cmd shim cannot drive — picking one
        // hangs every run at the initialize handshake.
        ProviderCliResolver.Resolve("shimmed").Should().Be(exe);
    }

    [Fact]
    public void Resolve_SkipsTheExtensionlessShShim_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // npm drops a POSIX sh script named like the tool next to its .cmd/.ps1 shims.
        // Neither CreateProcess nor cmd.exe can run it, so counting it made the probe
        // report "ready" for a CLI the spawn then failed with "'copilot' is not recognized".
        Touch("shimonly");
        SetVar("PATH", _dir);

        ProviderCliResolver.Resolve("shimonly").Should().BeNull();

        var cmd = Touch("shimonly.cmd");
        ProviderCliResolver.Resolve("shimonly").Should().Be(cmd);
    }

    [Fact]
    public void LaunchFor_RoutesScriptShimsThroughTheirInterpreter_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var exe = ProviderCliResolver.LaunchFor(@"C:\x\tool.exe");
        exe.FileName.Should().Be(@"C:\x\tool.exe");
        exe.ArgumentPrefix.Should().BeEmpty();

        var cmd = ProviderCliResolver.LaunchFor(@"C:\x\tool.cmd");
        cmd.FileName.Should().Be("cmd.exe");
        cmd.ArgumentPrefix.Should().Equal("/c", @"C:\x\tool.cmd");
        cmd.CliPath.Should().Be(@"C:\x\tool.cmd");

        var ps1 = ProviderCliResolver.LaunchFor(@"C:\x\tool.ps1");
        Path.GetFileName(ps1.FileName).Should().BeOneOf("pwsh.exe", "powershell.exe");
        ps1.ArgumentPrefix.Should().Equal("-NoProfile", "-File", @"C:\x\tool.ps1");
    }

    [Fact]
    public void LaunchFor_NeverBypassesTheExecutionPolicy_AndPrefersTheCmdShimBesideAPs1()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var ps1 = Path.Combine(_dir, "both.ps1");
        var cmd = Path.Combine(_dir, "both.cmd");
        File.WriteAllText(ps1, "");
        File.WriteAllText(cmd, "");

        var launch = ProviderCliResolver.LaunchFor(ps1);
        launch.FileName.Should().Be("cmd.exe");
        launch.ArgumentPrefix.Should().Equal("/c", cmd);

        foreach (var path in new[] { ps1, @"C:\x\tool.ps1", @"C:\x\tool.cmd", @"C:\x\tool.exe" })
            ProviderCliResolver.LaunchFor(path).ArgumentPrefix.Should().NotContain(
                a => a.Equals("Bypass", StringComparison.OrdinalIgnoreCase)
                    || a.Equals("-ExecutionPolicy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LaunchFor_SpawnsDirectly_OffWindows()
    {
        if (OperatingSystem.IsWindows())
            return;

        var launch = ProviderCliResolver.LaunchFor("/usr/local/bin/tool");
        launch.FileName.Should().Be("/usr/local/bin/tool");
        launch.ArgumentPrefix.Should().BeEmpty();
    }

    [Fact]
    public void ResolveLaunch_IsNull_ExactlyWhenResolveIs()
    {
        SetVar("PATH", _dir);
        ProviderCliResolver.ResolveLaunch("definitely-not-a-real-cli").Should().BeNull();

        var exe = Touch(OperatingSystem.IsWindows() ? "faketool.exe" : "faketool");
        ProviderCliResolver.ResolveLaunch("faketool")!.FileName.Should().Be(exe);
    }

    [Fact]
    public void Resolve_IsEmpty_ForAnEmptyProviderName()
    {
        SetVar("PATH", _dir);
        ProviderCliResolver.Resolve("").Should().BeNull();
        ProviderCliResolver.Resolve("   ").Should().BeNull();
    }

    public void Dispose()
    {
        ProviderCliResolver.ReadEnv = Environment.GetEnvironmentVariable;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
