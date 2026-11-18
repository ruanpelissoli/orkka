namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Usage.Infrastructure;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Runners.Domain;
using Orkka.Modules.Runners.Infrastructure;
using Orkka.SharedKernel.Ai;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Customer utility AI runs on the developer's runner only. With the runner offline a utility feature
/// degrades (here: the chat stays untitled) — nothing else answers it.
/// </summary>
[Collection("Integration")]
public sealed class RunnerOnlyUtilityTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private FakeChatAgentInvoker Fake =>
        (FakeChatAgentInvoker)factory.Services.GetRequiredService<IChatAgentInvoker>();

    [Fact]
    public async Task AnOfflineRunner_IsAMiss_AndNothingElseAnswers()
    {
        var orgId = Ulid.NewUlid();
        var userId = $"user_{Ulid.NewUlid()}";

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnersDbContext>();
        // A runner is attached but connected nowhere (no presence row) — the laptop is closed.
        db.RunnerTokens.Add(new RunnerToken
        {
            Id = Ulid.NewUlid(),
            OrgId = orgId,
            UserId = userId,
            Name = "laptop",
            TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        });
        await db.SaveChangesAsync();

        var utility = scope.ServiceProvider.GetRequiredService<IUserRunnerUtility>();
        var request = new AnthropicRequest
        {
            Model = "claude-haiku-4-5",
            MaxTokens = 50,
            Messages = [new AnthropicMessage("user", "Title this")],
        };

        (await utility.TryCompleteAsync(orgId, userId, request)).Should().BeNull();

        var act = () => utility.CompleteOnRunnerAsync(orgId, userId, request, "plan this milestone");
        (await act.Should().ThrowAsync<RunnerUtilityUnavailableException>())
            .Which.Message.Should().Contain("./orkka start");

    }

    [Fact]
    public async Task AChatTurn_WithNoRunner_StaysUntitled_AndNothingElseAnswers()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest($"Runner Only {Ulid.NewUlid()}", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var agentId = Ulid.NewUlid();
        var conversationId = Ulid.NewUlid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            db.ProjectAgents.Add(new ProjectAgent
            {
                Id = agentId,
                OrgId = TestCurrentUser.TestOrgId,
                ProjectId = project!.Id,
                Name = "Archie",
                Role = AgentRole.SolutionArchitect,
                Model = "claude-sonnet-4-6",
            });
            db.AgentConversations.Add(new AgentConversation
            {
                Id = conversationId,
                OrgId = TestCurrentUser.TestOrgId,
                ProjectId = project.Id,
                AgentId = agentId,
            });
            await db.SaveChangesAsync();
        }

        Fake.EnqueueChunks(
            new ChatChunk(ChatChunkType.Text, "Here is the architecture."),
            new ChatChunk(ChatChunkType.Done, "", PromptTokens: 10, CompletionTokens: 5));

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project!.Id}/agents/{agentId}/conversations/{conversationId}/messages",
            new SendMessageRequest("design the system"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The turn's own usage row is written AFTER the titling step, so once it exists the
        // titler has had its chance.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var turnRecorded = false;
        while (DateTime.UtcNow < deadline && !turnRecorded)
        {
            using var scope = factory.Services.CreateScope();
            var billing = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            turnRecorded = await billing.UsageRecords.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(u => u.ProjectId == project.Id && u.AgentId == agentId);
            if (!turnRecorded) await Task.Delay(100);
        }
        turnRecorded.Should().BeTrue("the chat turn itself must still complete and be recorded");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var conversation = await db.AgentConversations.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(c => c.Id == conversationId);
            conversation.Title.Should().BeNull("titling needs the runner, and there is no fallback");

            var billing = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            (await billing.UsageRecords.IgnoreQueryFilters().AsNoTracking()
                    .CountAsync(u => u.ProjectId == project.Id))
                .Should().Be(1, "only the turn is recorded — no utility call was served anywhere");
        }

    }
}
