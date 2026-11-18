namespace Orkka.Tests.Features.ProjectRepos;

using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Domain;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Contracts;
using FluentAssertions;
using Orkka.SharedKernel.Utilities;

public sealed class ProjectRepoMappersTests
{
    [Theory]
    [InlineData(ForgeKind.GitHub, "https://github.com/org/repo.git", "github")]
    [InlineData(ForgeKind.AzureDevOps, "https://dev.azure.com/org/p/_git/repo", "azuredevops")]
    [InlineData(ForgeKind.GitLab, "git@gitlab.com:org/repo.git", "gitlab")]
    [InlineData(ForgeKind.Other, "git@github-work:org/repo.git", "other")]
    [InlineData(ForgeKind.Other, @"C:\Code\repo", "local")]
    public void ToResponse_NamesTheForge_OrLocalForAFolder(ForgeKind forge, string url, string expected)
    {
        var entity = CreateRepo(forge, url);
        var response = entity.ToResponse();
        response.Provider.Should().Be(expected);
    }

    [Theory]
    [InlineData(WorkspaceStatus.Pending, "active")]
    [InlineData(WorkspaceStatus.Ready, "active")]
    [InlineData(WorkspaceStatus.Cloning, "cloning")]
    [InlineData(WorkspaceStatus.Error, "error")]
    public void ToResponse_MapsWorkspaceStatusCorrectly(WorkspaceStatus status, string expected)
    {
        var entity = CreateRepo(workspaceStatus: status);
        var response = entity.ToResponse();
        response.WorkspaceStatus.Should().Be(expected);
    }

    [Fact]
    public void ToResponse_MapsAllFields()
    {
        var entity = CreateRepo();
        var response = entity.ToResponse();

        response.Id.Should().Be(entity.Id);
        response.ProjectId.Should().Be(entity.ProjectId);
        response.Name.Should().Be(entity.Name);
        response.GitUrl.Should().Be(entity.GitUrl);
        response.DefaultBranch.Should().Be(entity.DefaultBranch);
        response.CardColor.Should().Be(entity.CardColor);
        response.CreatedAt.Should().Be(entity.CreatedAt);
    }

    [Fact]
    public void RandomCardColor_ReturnsValidHexColor()
    {
        var color = Mappers.RandomCardColor();
        color.Should().StartWith("#");
        color.Should().HaveLength(7);
    }

    [Theory]
    [InlineData(null, "https://github.com/org/repo.git", ForgeKind.GitHub)]
    [InlineData(null, "git@ssh.dev.azure.com:v3/org/p/repo", ForgeKind.AzureDevOps)]
    [InlineData("github", "git@github-work:org/repo.git", ForgeKind.GitHub)]
    [InlineData("ADO", "https://git.internal/org/repo", ForgeKind.AzureDevOps)]
    [InlineData("gitlab", "https://git.internal/org/repo", ForgeKind.GitLab)]
    [InlineData("unknown", "https://bitbucket.org/org/repo", ForgeKind.Bitbucket)]
    public void ParseForge_HonoursAnOverride_ElseReadsTheHost(string? input, string url, ForgeKind expected)
    {
        Mappers.ParseForge(input, url).Should().Be(expected);
    }

    private static ProjectRepo CreateRepo(
        ForgeKind forge = ForgeKind.GitHub,
        string gitUrl = "https://github.com/org/repo.git",
        WorkspaceStatus workspaceStatus = WorkspaceStatus.Pending) => new()
    {
        Id = Ulid.NewUlid(),
        OrgId = Ulid.NewUlid(),
        ProjectId = Ulid.NewUlid(),
        Name = "test-repo",
        GitUrl = gitUrl,
        DefaultBranch = "main",
        ForgeKind = forge,
        WorkspaceStatus = workspaceStatus,
        CardColor = "#4F46E5",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
