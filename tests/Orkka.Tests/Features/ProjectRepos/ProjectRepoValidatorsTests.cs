namespace Orkka.Tests.Features.ProjectRepos;

using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Domain;
using FluentValidation.TestHelper;

public sealed class ProjectRepoValidatorsTests
{
    private readonly AddProjectRepoValidator _validator = new();

    [Fact]
    public void Valid_Input_Passes()
    {
        var request = new AddProjectRepoRequest(
            "my-repo", "https://github.com/org/repo.git", "main", "github", null);
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyName_Fails()
    {
        var request = new AddProjectRepoRequest(
            "", "https://github.com/org/repo.git", "main", null, null);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void NameTooLong_Fails()
    {
        var request = new AddProjectRepoRequest(
            new string('a', 201), "https://github.com/org/repo.git", "main", null, null);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void EmptyGitUrl_Fails()
    {
        var request = new AddProjectRepoRequest(
            "repo", "", "main", null, null);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.GitUrl);
    }

    [Theory]
    [InlineData("git@github.com:acme/api.git")]
    [InlineData("git@github-work:acme/api.git")]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/acme/Shop/api")]
    [InlineData("https://acme@dev.azure.com/acme/Shop/_git/api")]
    public void TheDevelopersOwnRemotes_Pass(string url)
    {
        // The last one is how Azure DevOps hands out clone URLs: a user name, no secret.
        var request = new AddProjectRepoRequest("repo", url, "main", null, null);
        _validator.TestValidate(request).ShouldNotHaveValidationErrorFor(x => x.GitUrl);
    }

    [Theory]
    [InlineData(@"C:\code\api")]
    [InlineData("/home/dana/api")]
    [InlineData("https://dana:ghp_secret@github.com/acme/api.git")]
    public void FoldersAndCredentials_Fail(string url)
    {
        var request = new AddProjectRepoRequest("repo", url, "main", null, null);
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.GitUrl);
    }

    [Fact]
    public void InvalidGitUrl_Fails()
    {
        var request = new AddProjectRepoRequest(
            "repo", "not-a-url", "main", null, null);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.GitUrl);
    }

    [Fact]
    public void GitUrlTooLong_Fails()
    {
        var request = new AddProjectRepoRequest(
            "repo", "https://github.com/" + new string('a', 2040), "main", null, null);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.GitUrl);
    }

    [Fact]
    public void EmptyDefaultBranch_Fails()
    {
        var request = new AddProjectRepoRequest(
            "repo", "https://github.com/org/repo.git", "", null, null);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.DefaultBranch);
    }

    [Fact]
    public void DefaultBranchTooLong_Fails()
    {
        var request = new AddProjectRepoRequest(
            "repo", "https://github.com/org/repo.git", new string('a', 101), null, null);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.DefaultBranch);
    }
}
