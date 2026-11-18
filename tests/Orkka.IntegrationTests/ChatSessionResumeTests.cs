namespace Orkka.IntegrationTests;

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Features.Projects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Exercises warm-session reuse in agent chat (SendMessageEndpoint): the claude session id
/// reported by the first run is persisted on the conversation and passed as resume on the
/// next turn (with history injection skipped), and a failed resume falls back to a fresh
/// session with the full history-injected prompt without surfacing the intermediate error.
/// Joins the Integration collection (own factory, shared TestCurrentUser statics) so it
/// never runs concurrently with tests that flip roles/permissions.
/// </summary>
[Collection("Integration")]
public sealed class ChatSessionResumeTests : IAsyncLifetime
{
    private readonly OrkkaApiFactory _factory = new();
    private readonly FakeChatInvoker _fake = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory.ConfigureTestServices = services =>
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IChatAgentInvoker));
            if (descriptor is not null)
                services.Remove(descriptor);
            services.AddSingleton<IChatAgentInvoker>(_fake);
        };

        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AgentChat_ReusesClaudeSession_AndFallsBackWhenResumeFails()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Session Resume Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        // Own agent row (instead of a seeded one) so the role has no post-stream side
        // effects (diagram/task extraction) muddying the SSE assertions.
        var agentId = Ulid.NewUlid();
        using (var scope = _factory.Services.CreateScope())
        {
            var agentsDb = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            agentsDb.ProjectAgents.Add(new ProjectAgent
            {
                Id = agentId,
                OrgId = TestCurrentUser.TestOrgId,
                ProjectId = project!.Id,
                Name = "Scout",
                Role = AgentRole.QA,
                Model = "claude-sonnet-4-6",
                SystemPrompt = "You are a QA agent."
            });
            await agentsDb.SaveChangesAsync();
        }

        var convResp = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/{agentId}/conversations",
            new CreateConversationRequest());
        var conversation = await convResp.Content.ReadFromJsonAsync<ConversationResponse>(TestJsonOptions.Default);
        var conversationId = Ulid.Parse(conversation!.Id);

        var messagesUrl =
            $"/api/projects/{project.Id}/agents/{agentId}/conversations/{conversationId}/messages";

        // ── Turn 1: no stored session — fresh run, its session id gets persisted ────────
        var sse1 = await PostMessageAsync(messagesUrl, "Hello");

        _fake.Calls.Should().HaveCount(1);
        _fake.Calls[0].ResumeSessionId.Should().BeNull();
        sse1.Should().Contain("Reply (fresh)");
        (await GetStoredSessionIdAsync(conversationId)).Should().Be("sess-1");

        // ── Turn 2: resumes the warm session; history is NOT re-injected ────────────────
        var sse2 = await PostMessageAsync(messagesUrl, "And again?");

        _fake.Calls.Should().HaveCount(2);
        _fake.Calls[1].ResumeSessionId.Should().Be("sess-1");
        _fake.Calls[1].SystemPrompt.Should().NotContain("## Conversation History");
        sse2.Should().Contain("Reply (resumed)");
        (await GetStoredSessionIdAsync(conversationId)).Should().Be("sess-1");

        // ── Turn 3: session file is gone (runner changed / cleanup) — the resume attempt
        // fails, the turn retries with a fresh session + full history, no error surfaces ─
        _fake.FailResumeAttempts = true;
        _fake.NextSessionId = "sess-2";

        var sse3 = await PostMessageAsync(messagesUrl, "One more");

        _fake.Calls.Should().HaveCount(4);
        _fake.Calls[2].ResumeSessionId.Should().Be("sess-1");
        _fake.Calls[3].ResumeSessionId.Should().BeNull();
        _fake.Calls[3].SystemPrompt.Should().Contain("## Conversation History");
        sse3.Should().NotContain("\"error\"");
        sse3.Should().Contain("Reply (fresh)");
        (await GetStoredSessionIdAsync(conversationId)).Should().Be("sess-2");

        // ── Turn 4: agent switched to a non-Claude model — no resume, full history, and
        // the stored claude session is dropped (its transcript won't contain this turn) ──
        _fake.FailResumeAttempts = false;
        await SetAgentModelAsync(agentId, "gemini-2.5-pro");

        await PostMessageAsync(messagesUrl, "Now on Gemini");

        _fake.Calls.Should().HaveCount(5);
        _fake.Calls[4].ResumeSessionId.Should().BeNull();
        _fake.Calls[4].SystemPrompt.Should().Contain("## Conversation History");
        (await GetStoredSessionIdAsync(conversationId)).Should().BeNull();

        // ── Turn 5: back on Claude — starts fresh (no stale resume) and stores anew ──────
        await SetAgentModelAsync(agentId, "claude-sonnet-4-6");
        _fake.NextSessionId = "sess-3";

        await PostMessageAsync(messagesUrl, "Back on Claude");

        _fake.Calls.Should().HaveCount(6);
        _fake.Calls[5].ResumeSessionId.Should().BeNull();
        (await GetStoredSessionIdAsync(conversationId)).Should().Be("sess-3");
    }

    private async Task SetAgentModelAsync(Ulid agentId, string model)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var agent = await db.ProjectAgents.IgnoreQueryFilters().FirstAsync(a => a.Id == agentId);
        agent.Model = model;
        await db.SaveChangesAsync();
    }

    private async Task<string> PostMessageAsync(string url, string content)
    {
        var response = await _client.PostAsJsonAsync(url, new SendMessageRequest(content));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string?> GetStoredSessionIdAsync(Ulid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var conversation = await db.AgentConversations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstAsync(c => c.Id == conversationId);
        return conversation.ClaudeSessionId;
    }

    private sealed class FakeChatInvoker : IChatAgentInvoker
    {
        public List<(string SystemPrompt, string? ResumeSessionId)> Calls { get; } = [];
        public bool FailResumeAttempts { get; set; }
        public string NextSessionId { get; set; } = "sess-1";

        public Task<ChatWorkspacePreparation> EnsureChatWorkspaceAsync(
            Ulid orgId, Ulid projectId, IReadOnlyList<ChatWorkspaceRepo> repos,
            Ulid? taskId = null, CancellationToken ct = default)
            => Task.FromResult(ChatWorkspacePreparation.Complete("fake-workspace"));

        public async IAsyncEnumerable<ChatChunk> InvokeAgentAsync(
            string workspacePath, string systemPrompt, string userMessage,
            string model, string? resumeSessionId = null, Ulid? conversationId = null,
            IChatToolServer? toolServer = null,
            ChatToolServerContext? toolContext = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls.Add((systemPrompt, resumeSessionId));
            await Task.CompletedTask;

            if (FailResumeAttempts && resumeSessionId is not null)
            {
                // Shape of a runner whose claude CLI can't find the session file.
                yield return new ChatChunk(ChatChunkType.Error,
                    $"Agent process failed: No conversation found with session ID: {resumeSessionId}");
                yield return new ChatChunk(ChatChunkType.Done, "");
                yield break;
            }

            yield return new ChatChunk(ChatChunkType.Text,
                resumeSessionId is null ? "Reply (fresh)" : "Reply (resumed)");
            yield return new ChatChunk(ChatChunkType.Done, "", 10, 5, 0, 0,
                resumeSessionId ?? NextSessionId);
        }
    }
}
