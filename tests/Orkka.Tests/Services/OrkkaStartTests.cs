namespace Orkka.Tests.Services;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Orkka.Cli;
using Orkka.LocalRunner.Cli;
using Orkka.LocalRunner.Runtime;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// `./orkka start`'s building blocks: its options, the secrets it hands the host in memory, the
/// one-instance-per-home lock and instance file, the runner profiles the bootstrap writes, and
/// the leftovers of the old installer `doctor` reports. Everything runs in a temp Orkka root,
/// which always gets the owner-only file secret store.
/// </summary>
public sealed class OrkkaStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-start-" + Guid.NewGuid().ToString("N"));

    public OrkkaStartTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Options ────────────────────────────────────────────────────────────────

    [Fact]
    public void Start_defaults_to_port_6750_and_opens_the_browser()
    {
        var options = StartCommand.Parse([], out var error)!;
        error.Should().BeNull();
        options.Port.Should().Be(6750);
        options.OpenBrowser.Should().BeTrue();
    }

    [Theory]
    [InlineData(new[] { "--port", "6791", "--no-browser" }, 6791, false)]
    [InlineData(new[] { "--port=7000" }, 7000, true)]
    public void Start_takes_a_port_and_no_browser(string[] args, int port, bool browser)
    {
        var options = StartCommand.Parse(args, out _)!;
        options.Port.Should().Be(port);
        options.OpenBrowser.Should().Be(browser);
    }

    [Theory]
    [InlineData("--port", "80")]
    [InlineData("--port", "nope")]
    [InlineData("--detach", null)]
    [InlineData("--offline", null)]
    public void Bad_or_retired_start_options_are_refused(string flag, string? value)
    {
        string[] args = value is null ? [flag] : [flag, value];
        StartCommand.Parse(args, out var error).Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    // ── Secrets ────────────────────────────────────────────────────────────────

    [Fact]
    public void Secrets_are_generated_once_and_kept_in_the_secret_store_not_in_config()
    {
        var first = LocalSecrets.Open(_root).EnsureAll();
        var second = LocalSecrets.Open(_root).EnsureAll();

        second.InstallSecret.Should().Be(first.InstallSecret);
        second.EncryptionKey.Should().Be(first.EncryptionKey);
        Convert.FromBase64String(first.EncryptionKey).Should().HaveCount(32, "Encryption:Key is a base64 256-bit key");
        first.ToString().Should().NotContain(first.InstallSecret);

        // A sandbox root gets the owner-only file store under the reserved scope folder…
        var dir = Path.Combine(_root, "workspaces", WorkspaceSecretStores.LocalStackScope, "secrets");
        File.ReadAllText(Path.Combine(dir, LocalSecrets.InstallSecretId)).Should().Be(first.InstallSecret);
        // …which is never mistaken for a workspace.
        WorkspaceProfiles.WorkspaceNames(_root).Should().BeEmpty();
    }

    [Fact]
    public void The_retired_proxy_secret_is_forgotten_on_start()
    {
        var store = WorkspaceSecretStores.CreateDefault(_root, WorkspaceSecretStores.LocalStackScope);
        store.Set(LocalSecrets.LegacyProxySecretId, "old-proxy-secret");

        LocalSecrets.Open(_root).EnsureAll();

        store.Get(LocalSecrets.LegacyProxySecretId).Should().BeNull();
    }

    [Fact]
    public void The_host_gets_its_secrets_as_in_memory_settings()
    {
        var secrets = LocalSecrets.Open(_root).EnsureAll();

        var settings = StartCommand.HostSettings(6791, secrets);

        settings["Local:Port"].Should().Be("6791");
        settings[Orkka.Local.LocalSettings.InstallSecretKey].Should().Be(secrets.InstallSecret);
        settings["Encryption:Key"].Should().Be(secrets.EncryptionKey);
        Environment.GetEnvironmentVariables().Values.Cast<object?>().Select(v => v?.ToString())
            .Should().NotContain(secrets.InstallSecret).And.NotContain(secrets.EncryptionKey);
    }

    [Fact]
    public void Local_secrets_have_their_own_keychain_names()
    {
        WorkspaceSecretStores.KeyFor(WorkspaceSecretStores.LocalStackScope, LocalSecrets.InstallSecretId)
            .Should().Be("orkka/local/install-secret");
        WorkspaceSecretStores.KeyFor("acme", "install-secret").Should().Be("orkka/acme/tracker/install-secret");
        WorkspaceProfiles.IsValidName(WorkspaceSecretStores.LocalStackScope).Should().BeFalse();
    }

    // ── One instance per Orkka home ──────────────────────────────────────────────

    [Fact]
    public void The_lock_is_held_by_one_start_at_a_time()
    {
        using (var first = OrkkaInstance.TryLock(_root))
        {
            first.Should().NotBeNull();
            OrkkaInstance.TryLock(_root).Should().BeNull("a second start must not run next to the first");
        }
        using var again = OrkkaInstance.TryLock(_root);
        again.Should().NotBeNull("the lock is released when its holder ends");
    }

    [Fact]
    public void The_instance_file_names_this_process_and_round_trips()
    {
        using var self = Process.GetCurrentProcess();
        var info = new InstanceInfo(Environment.ProcessId, 6791, DateTimeOffset.UtcNow, OrkkaInstance.StartTimeOf(self)!.Value);

        OrkkaInstance.Write(_root, info);

        OrkkaInstance.ReadAlive(_root).Should().Be(info);
        info.Url.Should().Be("http://127.0.0.1:6791");
        OrkkaInstance.Delete(_root);
        OrkkaInstance.Read(_root).Should().BeNull();
    }

    [Fact]
    public void A_pid_reused_by_another_process_is_not_ours()
    {
        using var self = Process.GetCurrentProcess();
        OrkkaInstance.Write(_root, new InstanceInfo(Environment.ProcessId, 6791, DateTimeOffset.UtcNow,
            OrkkaInstance.StartTimeOf(self)!.Value - TimeSpan.FromHours(1)));

        OrkkaInstance.ReadAlive(_root).Should().BeNull();
    }

    [Fact]
    public void A_corrupt_instance_file_reads_as_not_running()
    {
        Directory.CreateDirectory(OrkkaInstance.RunDir(_root));
        File.WriteAllText(OrkkaInstance.InfoFile(_root), "{ not json");

        OrkkaInstance.Read(_root).Should().BeNull();
    }

    [Fact]
    public void A_listening_port_is_not_free()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        StartCommand.IsPortFree(port).Should().BeFalse();
        listener.Stop();
        StartCommand.IsPortFree(port).Should().BeTrue();
    }

    // ── Bootstrap → runner profiles ────────────────────────────────────────────

    [Fact]
    public void A_minted_token_becomes_a_profile_found_again_by_workspace_id()
    {
        const string host = "http://127.0.0.1:6750";
        var first = RunnerProfileSync.Apply(_root, host,
            [new BootstrapWorkspace { WorkspaceId = "01JWS1", Name = "Acme Corp", Token = "orkr_one" }]);

        first.Added.Should().Equal("acme-corp");
        var profile = WorkspaceProfiles.Get(_root, "acme-corp")!;
        profile.Token.Should().Be("orkr_one");
        profile.HostUrl.Should().Be(host);
        profile.WorkspaceId.Should().Be("01JWS1");
        File.ReadAllText(WorkspaceProfiles.ProfilePath(_root, "acme-corp")).Should().NotContain("orkr_one");

        // Later bootstraps: no token, but Orkka was started on another port.
        var moved = RunnerProfileSync.Apply(_root, "http://127.0.0.1:6760",
            [new BootstrapWorkspace { WorkspaceId = "01JWS1", Name = "Acme Corp", Token = null }]);
        moved.Updated.Should().Equal("acme-corp");
        WorkspaceProfiles.Get(_root, "acme-corp")!.HostUrl.Should().Be("http://127.0.0.1:6760");
        WorkspaceProfiles.Get(_root, "acme-corp")!.Token.Should().Be("orkr_one");
    }

    [Fact]
    public void Two_workspaces_with_the_same_name_get_distinct_profiles()
    {
        var outcome = RunnerProfileSync.Apply(_root, "http://127.0.0.1:6750",
        [
            new BootstrapWorkspace { WorkspaceId = "A", Name = "Acme", Token = "orkr_a" },
            new BootstrapWorkspace { WorkspaceId = "B", Name = "Acme", Token = "orkr_b" },
            new BootstrapWorkspace { WorkspaceId = "C", Name = "!!!", Token = "orkr_c" },
            new BootstrapWorkspace { WorkspaceId = "D", Name = "Gone", Token = null },
        ]);

        outcome.Added.Should().Equal("acme", "acme-2", "ws-c");
        outcome.MissingToken.Should().Equal("Gone");
        WorkspaceProfiles.Get(_root, "acme-2")!.WorkspaceId.Should().Be("B");
    }

    [Fact]
    public void The_runner_defaults_to_the_local_host()
    {
        ConnectCommand.DefaultHostUrl.Should().Be("http://127.0.0.1:6750");
        ConnectCommand.ParseArgs([ConnectCommand.AllowEmptyFlag]).AllowEmpty.Should().BeTrue();
        ConnectCommand.Resolve([ConnectCommand.AllowEmptyFlag], _root, out var error, out _)
            .Should().BeEmpty("under `start` the runner waits for the first workspace");
        error.Should().BeNull();
    }

    // ── The old installer's leftovers (doctor reports, never edits) ──────────────

    [Fact]
    public void A_clean_machine_has_no_findings()
    {
        LegacyInstall.Find(_root, _root, "/usr/bin" + Path.PathSeparator + "/bin", isWindows: false)
            .Should().BeEmpty();
    }

    [Fact]
    public void The_old_bin_folder_on_PATH_is_reported_with_manual_steps()
    {
        var bin = Path.Combine(_root, "bin");
        var findings = LegacyInstall.Find(_root, _root, "/usr/bin" + Path.PathSeparator + bin + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows());

        findings.Should().ContainSingle(f => f.Contains("PATH", StringComparison.Ordinal) && f.Contains(bin, StringComparison.Ordinal));
    }

    [Fact]
    public void Shell_lines_and_old_folders_are_reported_and_left_alone()
    {
        var rc = Path.Combine(_root, ".bashrc");
        string[] lines = ["alias ll='ls -l'", LegacyInstall.LegacyRcLine];
        File.WriteAllLines(rc, lines);
        Directory.CreateDirectory(Path.Combine(_root, "app"));

        var findings = LegacyInstall.Find(_root, _root, "", isWindows: false);

        findings.Should().Contain(f => f.Contains(".bashrc", StringComparison.Ordinal) && f.Contains("line 2", StringComparison.Ordinal));
        findings.Should().Contain(f => f.Contains(Path.Combine(_root, "app"), StringComparison.Ordinal));
        File.ReadAllLines(rc).Should().Equal(lines, "Orkka never edits a shell file");
        Directory.Exists(Path.Combine(_root, "app")).Should().BeTrue();
    }
}
