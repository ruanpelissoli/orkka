namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner;

/// <summary>
/// What the runner asks each forge CLI to do for a Story: open the pull request as a draft, mark
/// it ready, turn it back into a draft, rewrite its description. An ordinary task's pull request
/// is opened exactly as before — no draft flag.
/// </summary>
public sealed class ForgeCliStoryArgsTests
{
    private static readonly string[] AzureScope = ["--organization", "https://dev.azure.com/acme", "--project", "Shop", "--repository", "api"];

    [Fact]
    public void GitHub_DraftCreate_ReadyAndUndo_EditBody()
    {
        ForgeCliArgs.GitHubCreate("acme/api", "feature/S-1", "main", "t", "body.md", draft: true)
            .Should().ContainInOrder("pr", "create").And.EndWith("--draft");
        ForgeCliArgs.GitHubCreate("acme/api", "feature/S-1", "main", "t", "body.md", draft: false)
            .Should().NotContain("--draft");
        ForgeCliArgs.GitHubReady("https://github.com/acme/api/pull/7", "acme/api", undo: false)
            .Should().Equal("pr", "ready", "https://github.com/acme/api/pull/7", "--repo", "acme/api");
        ForgeCliArgs.GitHubReady("https://github.com/acme/api/pull/7", "acme/api", undo: true)
            .Should().Equal("pr", "ready", "https://github.com/acme/api/pull/7", "--repo", "acme/api", "--undo");
        ForgeCliArgs.GitHubEditBody("https://github.com/acme/api/pull/7", "acme/api", "body.md")
            .Should().Equal("pr", "edit", "https://github.com/acme/api/pull/7", "--repo", "acme/api", "--body-file", "body.md");
    }

    [Fact]
    public void AzureDevOps_DraftCreate_ReadyAndUndo_EditBody()
    {
        var create = ForgeCliArgs.AzureCreate(AzureScope, "feature/S-1", "main", "t", "b", draft: true);
        create.Should().ContainInOrder("repos", "pr", "create");
        create.Should().ContainInOrder("--draft", "true");
        create.Should().ContainInOrder("--query", "pullRequestId", "-o", "tsv");
        ForgeCliArgs.AzureCreate(AzureScope, "feature/S-1", "main", "t", "b", draft: false).Should().NotContain("--draft");

        ForgeCliArgs.AzureReady("42", "https://dev.azure.com/acme", undo: false)
            .Should().ContainInOrder("repos", "pr", "update", "--id", "42").And.ContainInOrder("--draft", "false");
        ForgeCliArgs.AzureReady("42", "https://dev.azure.com/acme", undo: true)
            .Should().ContainInOrder("--draft", "true");
        ForgeCliArgs.AzureEditBody("42", "https://dev.azure.com/acme", "new body")
            .Should().ContainInOrder("repos", "pr", "update", "--id", "42").And.ContainInOrder("--description", "new body");
    }

    [Fact]
    public void GitLab_DraftCreate_ReadyAndUndo_EditBody()
    {
        ForgeCliArgs.GitLabCreate("acme/api", "feature/S-1", "main", "t", "b", draft: true)
            .Should().ContainInOrder("mr", "create").And.Contain("--draft").And.EndWith("--yes");
        ForgeCliArgs.GitLabCreate("acme/api", "feature/S-1", "main", "t", "b", draft: false).Should().NotContain("--draft");
        ForgeCliArgs.GitLabReady("https://gitlab.com/acme/api/-/merge_requests/3", "acme/api", undo: false)
            .Should().Equal("mr", "update", "https://gitlab.com/acme/api/-/merge_requests/3", "--repo", "acme/api", "--ready");
        ForgeCliArgs.GitLabReady("https://gitlab.com/acme/api/-/merge_requests/3", "acme/api", undo: true)
            .Should().EndWith("--draft");
        ForgeCliArgs.GitLabEditBody("https://gitlab.com/acme/api/-/merge_requests/3", "acme/api", "b")
            .Should().ContainInOrder("mr", "update").And.ContainInOrder("--description", "b");
    }
}
