namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// The two switches the e2e harness uses to point a runner at a fake tracker on loopback and
/// keep its tokens out of the real OS keychain. One class, so its tests never interleave on the
/// process-wide environment; only loopback inputs and the opt-in store are affected by them.
/// </summary>
public sealed class TrackerTestOptInTests : IDisposable
{
    private readonly string? _insecureBefore = Environment.GetEnvironmentVariable(TrackerSites.AllowInsecureSiteVariable);
    private readonly string? _storeBefore = Environment.GetEnvironmentVariable(WorkspaceSecretStores.StoreOverrideVariable);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-optin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TrackerSites.AllowInsecureSiteVariable, _insecureBefore);
        Environment.SetEnvironmentVariable(WorkspaceSecretStores.StoreOverrideVariable, _storeBefore);
        WorkspaceSecretStores.CaptureOverride(_ => null);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void LoopbackSites_AreTakenVerbatim_OnlyWithTheSwitch()
    {
        Environment.SetEnvironmentVariable(TrackerSites.AllowInsecureSiteVariable, null);
        TrackerSites.NormalizeJiraSite("http://127.0.0.1:4100").Should().NotBe("http://127.0.0.1:4100");
        TrackerSites.NormalizeAdoOrg("http://localhost:4100/contoso").Should().BeNull();

        Environment.SetEnvironmentVariable(TrackerSites.AllowInsecureSiteVariable, "1");
        TrackerSites.NormalizeJiraSite("http://127.0.0.1:4100/").Should().Be("http://127.0.0.1:4100");
        TrackerSites.NormalizeAdoOrg("http://localhost:4100/contoso").Should().Be("http://localhost:4100/contoso");
        TrackerSites.NormalizeJiraSite("http://jira.example.com").Should().Be("https://jira.example.com",
            "the switch never allows clear text off this machine");
        TrackerSites.NormalizeJiraSite("acme").Should().Be("https://acme.atlassian.net");
    }

    [Fact]
    public void TheFileStore_CanBeForced()
    {
        Environment.SetEnvironmentVariable(WorkspaceSecretStores.StoreOverrideVariable, "file");

        var store = WorkspaceSecretStores.CreateDefault(_root, "default");

        store.Should().BeOfType<FileSecretStore>();
        ((FileSecretStore)store).Directory.Should().StartWith(_root);
    }

    [Fact]
    public void TheForcedFileStore_SurvivesTheRunnersEnvironmentScrub()
    {
        // `connect` deletes every *SECRET* variable from its own environment before any session
        // asks for a store; the override it was started with must still hold — on this machine's
        // own Orkka home too, where the OS keychain would otherwise be chosen.
        Environment.SetEnvironmentVariable(WorkspaceSecretStores.StoreOverrideVariable, "file");
        WorkspaceSecretStores.CaptureOverride();
        Environment.SetEnvironmentVariable(WorkspaceSecretStores.StoreOverrideVariable, null);

        var store = WorkspaceSecretStores.CreateDefault(Orkka.LocalRunner.Cli.RunnerEnvFile.OrkkaDir, "scrub-test");

        store.Should().BeOfType<FileSecretStore>();
    }
}
