namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.SharedKernel.Ai;

/// <summary>
/// Chat workspaces MUST be scoped per project. Worktrees persist between chats, so an
/// org-shared workspace accumulates every project's repos — and an agent whose Cwd is
/// that shared folder reads the wrong project's code and branding (a designer once
/// produced another project's avatars this way).
/// </summary>
public sealed class RunnerTokensChatWorkspaceTests
{
    [Fact]
    public void ChatWorkspace_WithProjectId_RoundTrips()
    {
        var token = RunnerTokens.ChatWorkspace("org-1", "proj-2");

        RunnerTokens.TryParseChatWorkspace(token, out var orgId, out var projectId).Should().BeTrue();
        orgId.Should().Be("org-1");
        projectId.Should().Be("proj-2");
    }

    [Fact]
    public void ChatWorkspace_LegacyOrgOnlyToken_ParsesWithNullProject()
    {
        // Tokens minted by pre-project-scoping runners/hosts must keep working.
        var token = RunnerTokens.ChatWorkspace("org-1");
        token.Should().Be("runner-chat:org-1");

        RunnerTokens.TryParseChatWorkspace(token, out var orgId, out var projectId).Should().BeTrue();
        orgId.Should().Be("org-1");
        projectId.Should().BeNull();
    }

    [Theory]
    [InlineData("runner-worktree:org:repo:task")] // different token kind
    [InlineData("runner-chat:")] // empty org
    [InlineData("runner-chat:org:")] // empty project
    [InlineData("runner-chat:org:proj:")] // empty task
    [InlineData("runner-chat:org:proj:task:extra")]
    [InlineData("garbage")]
    public void TryParseChatWorkspace_RejectsMalformedTokens(string token)
    {
        RunnerTokens.TryParseChatWorkspace(token, out _, out _).Should().BeFalse();
    }
}
