namespace Orkka.Tests.Local;

using System.Diagnostics;
using FluentAssertions;
using Orkka.LocalRunner;

/// <summary>
/// The forge a reviewed task is published to comes from the mirror's origin AS CONFIGURED —
/// a developer's url.*.insteadOf rewrite (a mirror, a local path) must not turn a GitHub
/// repository into "no remote to open a pull request on".
/// </summary>
public sealed class RunnerOriginUrlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-origin-" + Guid.NewGuid().ToString("N"));

    public RunnerOriginUrlTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task The_mirror_origin_is_the_configured_url_not_its_insteadOf_rewrite()
    {
        var bare = Path.Combine(_root, "bare");
        Git(_root, $"init --bare -q \"{bare}\"");
        Git(bare, "config remote.origin.url https://github.com/acme/api.git");
        var mirror = new Uri(Path.Combine(_root, "mirror") + Path.DirectorySeparatorChar).AbsoluteUri;
        Git(bare, $"config url.{mirror}.insteadOf https://github.com/acme/");

        (await RunnerOperations.GetOriginUrlAsync(bare)).Should().Be("https://github.com/acme/api.git");
    }

    [Fact]
    public async Task A_mirror_without_origin_has_none()
    {
        var bare = Path.Combine(_root, "bare");
        Git(_root, $"init --bare -q \"{bare}\"");

        (await RunnerOperations.GetOriginUrlAsync(bare)).Should().BeNull();
    }

    private static void Git(string dir, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed: {err}");
    }
}
