namespace Orkka.Tests.Utilities;

using FluentAssertions;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Developers paste remotes in every shape git accepts. The classification decides whether a
/// repo is cloned (remote) or used in place (folder), so an ssh remote read as a folder sends
/// the whole task down the wrong path.
/// </summary>
public sealed class RepoUrlTests
{
    [Theory]
    [InlineData("https://github.com/acme/app.git")]
    [InlineData("http://git.internal/acme/app")]
    [InlineData("ssh://git@github.com/acme/app.git")]
    [InlineData("git@github.com:acme/app.git")]
    [InlineData("git@github-work:acme/app.git")]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Shop/web")]
    [InlineData("https://acme@dev.azure.com/acme/Shop/_git/web")]
    [InlineData("gitlab.company.io:platform/api.git")]
    public void Remotes_ClassifyAsRemote(string url)
        => RepoUrl.Classify(url).Should().Be(RepoLocation.Remote);

    [Theory]
    [InlineData(@"C:\Code\app")]
    [InlineData("C:/Code/app")]
    [InlineData(@"\\server\share\app")]
    [InlineData("/home/dev/app")]
    [InlineData("~/code/app")]
    [InlineData("file:///home/dev/app")]
    [InlineData("./app")]
    [InlineData("")]
    [InlineData(null)]
    public void Folders_ClassifyAsLocal(string? url)
        => RepoUrl.Classify(url).Should().Be(RepoLocation.LocalFolder);

    [Theory]
    [InlineData("https://github.com/acme/app.git", ForgeKind.GitHub)]
    [InlineData("git@github.com:acme/app.git", ForgeKind.GitHub)]
    [InlineData("https://dev.azure.com/acme/Shop/_git/web", ForgeKind.AzureDevOps)]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Shop/web", ForgeKind.AzureDevOps)]
    [InlineData("https://acme.visualstudio.com/Shop/_git/web", ForgeKind.AzureDevOps)]
    [InlineData("https://gitlab.com/acme/platform/api.git", ForgeKind.GitLab)]
    [InlineData("git@gitlab.company.io:acme/api.git", ForgeKind.GitLab)]
    [InlineData("https://bitbucket.org/acme/app.git", ForgeKind.Bitbucket)]
    [InlineData("git@github-work:acme/app.git", ForgeKind.Other)]
    [InlineData(@"C:\Code\app", ForgeKind.Other)]
    public void DetectForge_ReadsTheHost(string url, ForgeKind expected)
        => RepoUrl.DetectForge(url).Should().Be(expected);

    [Theory]
    [InlineData("https://github.com/acme/app.git", "github.com", "acme", "app", null)]
    [InlineData("git@github.com:acme/app.git", "github.com", "acme", "app", null)]
    [InlineData("ssh://git@github.com/acme/app", "github.com", "acme", "app", null)]
    [InlineData("https://gitlab.com/acme/platform/api.git", "gitlab.com", "acme/platform", "api", null)]
    [InlineData("https://dev.azure.com/acme/Shop/_git/web", "dev.azure.com", "acme", "web", "Shop")]
    [InlineData("https://acme@dev.azure.com/acme/My%20Shop/_git/web", "dev.azure.com", "acme", "web", "My Shop")]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Shop/web", "ssh.dev.azure.com", "acme", "web", "Shop")]
    [InlineData("https://acme.visualstudio.com/DefaultCollection/Shop/_git/web", "acme.visualstudio.com", "acme", "web", "Shop")]
    public void ParseRef_ReadsOwnerNameAndProject(
        string url, string host, string owner, string name, string? project)
        => RepoUrl.ParseRef(url).Should().Be(new ForgeRepoRef(host, owner, name, project));

    [Fact]
    public void ParseRef_HonoursAForgeOverrideForAnSshAlias()
    {
        var parsed = RepoUrl.ParseRef("git@github-work:acme/app.git", ForgeKind.GitHub);

        parsed.Should().Be(new ForgeRepoRef("github-work", "acme", "app"));
        parsed!.Slug.Should().Be("acme/app");
    }

    [Theory]
    [InlineData(@"C:\Code\app")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://dev.azure.com/acme/Shop")]
    public void ParseRef_IsNullWhenThereIsNoRepoToName(string url)
        => RepoUrl.ParseRef(url).Should().BeNull();

    [Theory]
    [InlineData("https://dana:secret@github.com/acme/api.git", true)]
    [InlineData("https://ghp_abc123@github.com/acme/api.git", true)]
    [InlineData("https://oauth2:glpat-xyz@gitlab.com/acme/api.git", true)]
    // Azure DevOps puts the organisation in the user name — no secret.
    [InlineData("https://acme@dev.azure.com/acme/Shop/_git/api", false)]
    [InlineData("https://github.com/acme/api.git", false)]
    [InlineData("git@github.com:acme/api.git", false)]
    [InlineData("ssh://git@github.com/acme/api.git", false)]
    [InlineData(null, false)]
    public void HasEmbeddedCredential_FindsSecretsNotUserNames(string? url, bool expected)
        => RepoUrl.HasEmbeddedCredential(url).Should().Be(expected);
}
