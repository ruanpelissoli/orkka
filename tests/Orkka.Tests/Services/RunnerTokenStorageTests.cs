namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner.Cli;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// The runner token is a bearer credential for the developer's machine: it lives in the OS
/// secret store, never in a plain file. These tests run against a temp root, which always gets
/// the owner-only file store — a sandbox must never write into the real keychain.
/// </summary>
public sealed class RunnerTokenStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-tok-" + Guid.NewGuid().ToString("N"));

    public RunnerTokenStorageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string ProfileFile(string name) => WorkspaceProfiles.ProfilePath(_root, name);

    [Fact]
    public void Save_PutsTheTokenInTheSecretStore_NotInProfileEnv()
    {
        WorkspaceProfiles.Save(_root, new WorkspaceProfile { Name = "acme", HostUrl = "https://h", Token = "orkr_secret" });

        File.ReadAllText(ProfileFile("acme")).Should().NotContain("orkr_secret");
        WorkspaceSecretStores.CreateDefault(_root, "acme").Get(WorkspaceSecretStores.RunnerTokenId).Should().Be("orkr_secret");
        WorkspaceProfiles.Get(_root, "acme")!.Token.Should().Be("orkr_secret");
    }

    [Fact]
    public void AProfileWrittenByAnOlderRunner_MovesItsTokenIntoTheStore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfileFile("default"))!);
        File.WriteAllText(ProfileFile("default"),
            "ORKKA_HOSTED_URL=https://h\nORKKA_HOSTED_TOKEN=orkr_legacy\nORKKA_GIT_AUTHOR_NAME=Dana\nORKKA_GIT_AUTHOR_EMAIL=d@acme.com\n");

        var profile = WorkspaceProfiles.Get(_root, "default")!;

        profile.Token.Should().Be("orkr_legacy");
        profile.GitAuthorName.Should().Be("Dana");
        File.ReadAllText(ProfileFile("default")).Should().NotContain("orkr_legacy").And.Contain("ORKKA_GIT_AUTHOR_NAME=Dana");
        WorkspaceProfiles.Get(_root, "default")!.Token.Should().Be("orkr_legacy");
    }

    [Fact]
    public void SaveToken_ReplacesOnlyTheToken()
    {
        WorkspaceProfiles.Save(_root, new WorkspaceProfile
        {
            Name = "acme", HostUrl = "https://h", Token = "orkr_old", GitAuthorName = "Dana", GitAuthorEmail = "d@acme.com",
        });

        WorkspaceProfiles.SaveToken(_root, "acme", "orkr_rotated");

        var profile = WorkspaceProfiles.Get(_root, "acme")!;
        profile.Token.Should().Be("orkr_rotated");
        profile.HasIdentity.Should().BeTrue();
    }

    [Fact]
    public void DeleteToken_ForgetsTheWorkspace()
    {
        WorkspaceProfiles.Save(_root, new WorkspaceProfile { Name = "acme", HostUrl = "https://h", Token = "orkr_x" });

        WorkspaceProfiles.DeleteToken(_root, "acme", TextWriter.Null);

        WorkspaceProfiles.Get(_root, "acme").Should().BeNull("a profile without a token cannot connect");
    }
}
