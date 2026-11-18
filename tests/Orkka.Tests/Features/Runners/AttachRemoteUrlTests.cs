namespace Orkka.Tests.Features.Runners;

using Orkka.Modules.Runners.Features.LocalRepos;

/// <summary>
/// The remote a developer pastes when connecting a local folder to a repository they created.
/// It is written into the folder's git config and pushed to with their own credentials, so it
/// must be a real remote, and never one that carries a credential.
/// </summary>
public sealed class AttachRemoteUrlTests
{
    [Theory]
    [InlineData("https://github.com/ruan/my-app.git", "https://github.com/ruan/my-app.git")]
    [InlineData("https://github.com/ruan/my-app", "https://github.com/ruan/my-app")]
    [InlineData("  https://github.com/ruan/my-app  ", "https://github.com/ruan/my-app")]
    [InlineData("ruan/my-app", "https://github.com/ruan/my-app")]
    // The developer's own ssh setup, host aliases included, is exactly what should be used.
    [InlineData("git@github.com:ruan/my-app.git", "git@github.com:ruan/my-app.git")]
    [InlineData("git@github-work:acme/api.git", "git@github-work:acme/api.git")]
    [InlineData("ssh://git@github.com/ruan/my-app.git", "ssh://git@github.com/ruan/my-app.git")]
    [InlineData("https://dev.azure.com/acme/Shop/_git/api", "https://dev.azure.com/acme/Shop/_git/api")]
    // Deep links on the big forges collapse to the repository.
    [InlineData("https://github.com/ruan/my-app/tree/main/src", "https://github.com/ruan/my-app.git")]
    public void Accepts_What_Developers_Paste(string input, string expected)
    {
        var ok = AttachRepoRemoteEndpoint.TryNormalizeRemoteUrl(input, out var normalized);

        Assert.True(ok);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    // Credentials in the URL would be written into the folder's git config.
    [InlineData("https://user:pat@github.com/ruan/my-app.git")]
    // Not a remote: a folder, a bare name, an owner without a repository.
    [InlineData("C:\\code\\my-app")]
    [InlineData("/home/ruan/my-app")]
    [InlineData("my-app")]
    [InlineData("https://github.com/ruan")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_Anything_That_Is_Not_A_Clean_Remote(string? input)
    {
        var ok = AttachRepoRemoteEndpoint.TryNormalizeRemoteUrl(input, out var normalized);

        Assert.False(ok);
        Assert.Equal("", normalized);
    }
}
