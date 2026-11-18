namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Exercises detached agent-chat runs: a browser disconnect must never cancel the run or
/// lose the persisted reply, completion after a disconnect must raise an AgentReplied
/// notification, and an explicit cancel must still stop the run.
/// </summary>
[Collection("Integration")]
public sealed class AgentChatDetachedRunTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private FakeChatAgentInvoker Fake =>
        (FakeChatAgentInvoker)factory.Services.GetRequiredService<IChatAgentInvoker>();

    private MockTaskNotifier Notifier =>
        (MockTaskNotifier)factory.Services.GetRequiredService<ITaskNotifier>();

    private async Task<(Ulid ProjectId, Ulid AgentId, Ulid ConversationId)> SeedChatAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest($"Chat Detach {Ulid.NewUlid()}", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();

        var agent = new ProjectAgent
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            ProjectId = project!.Id,
            Name = "Archie",
            Role = AgentRole.SolutionArchitect,
            Model = "claude-sonnet-4-6",
        };
        var conversation = new AgentConversation
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            ProjectId = project.Id,
            AgentId = agent.Id,
        };
        db.ProjectAgents.Add(agent);
        db.AgentConversations.Add(conversation);
        await db.SaveChangesAsync();

        return (project.Id, agent.Id, conversation.Id);
    }

    private string MessagesUrl(Ulid projectId, Ulid agentId, Ulid conversationId) =>
        $"/api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages";

    private async Task<HttpResponseMessage> PostMessageStreamingAsync(
        Ulid projectId, Ulid agentId, Ulid conversationId, string content, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl(projectId, agentId, conversationId))
        {
            Content = JsonContent.Create(new SendMessageRequest(content)),
        };
        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<string> ReadUntilAsync(StreamReader reader, string marker)
    {
        var seen = new System.Text.StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (line is null)
                throw new InvalidOperationException($"SSE stream ended before '{marker}'. Seen: {seen}");
            seen.AppendLine(line);
            if (line.Contains(marker))
                return seen.ToString();
        }
    }

    /// <summary>
    /// Waits for a realtime event instead of assuming it has landed by the time its database row
    /// has. <c>NotificationPublisher.PublishAsync</c> persists FIRST, then awaits the webhook
    /// outbox and Slack, and only then publishes — so polling for the row and immediately reading
    /// SentEvents asserts on work that is genuinely still in flight. A developer machine hides
    /// that gap; a loaded CI runner does not.
    /// </summary>
    private async Task<T> WaitForEventAsync<T>(int skip, Func<T, bool> match, string what)
        where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            // Snapshot: other tests' detached runs may still be appending to the shared list,
            // and enumerating it mid-append throws rather than just missing an entry.
            object[] snapshot;
            try { snapshot = [.. Notifier.SentEvents]; }
            catch (InvalidOperationException) { await Task.Delay(50); continue; }

            if (snapshot.Skip(skip).OfType<T>().FirstOrDefault(match) is { } hit)
                return hit;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out waiting for {what}");
    }

    private async Task<T> WaitForAsync<T>(Func<IServiceProvider, Task<T?>> probe, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var result = await probe(scope.ServiceProvider);
            if (result is not null)
                return result;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out waiting for {what}");
    }

    [Fact]
    public async Task Disconnect_MidStream_ReplyIsStillPersisted_AndNotificationRaised()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync();
        var countBefore = Notifier.SentEvents.Count;

        var gate = Fake.EnqueueGatedScript(
            before: [new ChatChunk(ChatChunkType.Text, "part1 ")],
            after:
            [
                new ChatChunk(ChatChunkType.Text, "part2"),
                new ChatChunk(ChatChunkType.Done, "", PromptTokens: 100, CompletionTokens: 50),
            ]);

        try
        {
            // Connect, read the first streamed chunk, then drop the connection mid-reply.
            using var cts = new CancellationTokenSource();
            var response = await PostMessageStreamingAsync(projectId, agentId, conversationId, "hello", cts.Token);
            using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token)))
            {
                await ReadUntilAsync(reader, "part1");
                cts.Cancel();
            }
            response.Dispose();
        }
        finally
        {
            gate.TrySetResult();
        }

        // The detached run must finish and persist the FULL reply despite the disconnect.
        var message = await WaitForAsync(async sp =>
        {
            var db = sp.GetRequiredService<AgentsDbContext>();
            return await db.AgentMessages.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.Role == "assistant");
        }, "assistant message");
        message.Content.Should().Be("part1 part2");

        // Completion after a disconnect raises the AgentReplied notification (bell + toast).
        var notification = await WaitForAsync(async sp =>
        {
            var db = sp.GetRequiredService<OrganizationsDbContext>();
            return await db.Notifications.AsNoTracking()
                .FirstOrDefaultAsync(n => n.ConversationId == conversationId);
        }, "AgentReplied notification");
        notification.Type.Should().Be(NotificationType.AgentReplied);
        notification.UserId.Should().Be(TestCurrentUser.TestUserId);
        notification.AgentId.Should().Be(agentId);
        notification.Title.Should().Be("Archie");

        await WaitForEventAsync<NotificationCreatedEvent>(countBefore,
            e => e.Type == "AgentReplied" && e.ConversationId == conversationId,
            "AgentReplied realtime event");
        await WaitForEventAsync<AgentChatMessageEvent>(countBefore,
            e => e.ConversationId == conversationId.ToString() && e.Content == "part1 part2",
            "AgentChatMessage realtime event");

        // The thinking marker must be cleared once the run finished.
        await WaitForAsync<object>(async sp =>
        {
            var db = sp.GetRequiredService<AgentsDbContext>();
            var conv = await db.AgentConversations.AsNoTracking().FirstAsync(c => c.Id == conversationId);
            return conv.ActiveRunStartedAt is null ? new object() : null;
        }, "active-run marker cleared");
    }

    [Fact]
    public async Task ConnectedRun_CompletesOverSse_WithoutNotification()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync();
        var countBefore = Notifier.SentEvents.Count;

        Fake.EnqueueChunks(
            new ChatChunk(ChatChunkType.Text, "streamed reply"),
            new ChatChunk(ChatChunkType.Done, "", PromptTokens: 10, CompletionTokens: 5));

        var response = await _client.PostAsJsonAsync(
            MessagesUrl(projectId, agentId, conversationId), new SendMessageRequest("hi"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("streamed reply");
        body.Should().Contain("\"done\":true");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var message = await db.AgentMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.Role == "assistant");
        message!.Content.Should().Be("streamed reply");

        // The user watched the reply stream in — no bell notification for THIS
        // conversation (other tests' detached runs may notify concurrently).
        Notifier.SentEvents.Skip(countBefore).OfType<NotificationCreatedEvent>()
            .Should().NotContain(e => e.Type == "AgentReplied" && e.ConversationId == conversationId);
    }

    [Fact]
    public async Task ConnectedRun_WithDeliverable_RaisesNotification()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync();
        var countBefore = Notifier.SentEvents.Count;

        // A ```document block makes the turn ship a deliverable (extraction runs for
        // every agent role).
        const string documentBlock =
            "```document\n{\"documents\":[{\"title\":\"Chat Doc\",\"path\":\"\",\"content\":\"# Hello\"}]}\n```";
        Fake.EnqueueChunks(
            new ChatChunk(ChatChunkType.Text, $"Saved it.\n{documentBlock}"),
            new ChatChunk(ChatChunkType.Done, "", PromptTokens: 10, CompletionTokens: 5));

        var response = await _client.PostAsJsonAsync(
            MessagesUrl(projectId, agentId, conversationId), new SendMessageRequest("write a doc"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // A deliverable-producing run notifies even though the observer stayed connected —
        // artifact runs are long and a live stream doesn't mean the user is watching.
        var notification = await WaitForAsync(async sp =>
        {
            var db = sp.GetRequiredService<OrganizationsDbContext>();
            return await db.Notifications.AsNoTracking()
                .FirstOrDefaultAsync(n => n.ConversationId == conversationId);
        }, "AgentReplied notification for connected deliverable run");
        notification.Type.Should().Be(NotificationType.AgentReplied);
        notification.UserId.Should().Be(TestCurrentUser.TestUserId);

        await WaitForEventAsync<NotificationCreatedEvent>(countBefore,
            e => e.Type == "AgentReplied" && e.ConversationId == conversationId,
            "AgentReplied realtime event for the connected deliverable run");
    }

    [Fact]
    public async Task ExplicitCancel_StopsRun_PersistsPartial_NoNotification()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync();
        var countBefore = Notifier.SentEvents.Count;

        var gate = Fake.EnqueueGatedScript(
            before: [new ChatChunk(ChatChunkType.Text, "partial reply")],
            after: [new ChatChunk(ChatChunkType.Done, "")]);

        try
        {
            var response = await PostMessageStreamingAsync(projectId, agentId, conversationId, "hello");
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            await ReadUntilAsync(reader, "partial reply");

            var cancelResp = await _client.PostAsync(
                $"/api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/cancel-run", null);
            cancelResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var cancelBody = await cancelResp.Content.ReadFromJsonAsync<CancelChatRunResponse>(TestJsonOptions.Default);
            cancelBody!.Cancelled.Should().BeTrue();

            // The observer sees the cancelled done event before the stream closes.
            var rest = await ReadUntilAsync(reader, "\"cancelled\":true");
            rest.Should().Contain("\"done\":true");
        }
        finally
        {
            gate.TrySetResult();
        }

        // The partial text survives in history; no notification for a user-initiated stop.
        var message = await WaitForAsync(async sp =>
        {
            var db = sp.GetRequiredService<AgentsDbContext>();
            return await db.AgentMessages.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.Role == "assistant");
        }, "partial assistant message");
        message.Content.Should().Be("partial reply");

        Notifier.SentEvents.Skip(countBefore).OfType<NotificationCreatedEvent>()
            .Should().NotContain(e => e.Type == "AgentReplied" && e.ConversationId == conversationId);
    }

    [Fact]
    public async Task SecondMessage_WhileRunActive_IsRejected()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync();

        var gate = Fake.EnqueueGatedScript(
            before: [new ChatChunk(ChatChunkType.Text, "busy")],
            after: [new ChatChunk(ChatChunkType.Done, "")]);

        try
        {
            var first = await PostMessageStreamingAsync(projectId, agentId, conversationId, "first");
            using var reader = new StreamReader(await first.Content.ReadAsStreamAsync());
            await ReadUntilAsync(reader, "busy");

            var second = await _client.PostAsJsonAsync(
                MessagesUrl(projectId, agentId, conversationId), new SendMessageRequest("second"));
            second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task ChatDeliverable_CountsAsCompletedTask_PlainReplyDoesNot()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync();

        // Plain reply — no deliverable, no task credit.
        Fake.EnqueueChunks(
            new ChatChunk(ChatChunkType.Text, "just words"),
            new ChatChunk(ChatChunkType.Done, ""));
        (await _client.PostAsJsonAsync(
            MessagesUrl(projectId, agentId, conversationId), new SendMessageRequest("hi")))
            .EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            (await db.ProjectAgents.AsNoTracking().FirstAsync(a => a.Id == agentId))
                .TasksCompleted.Should().Be(0);
        }

        // Reply shipping a diagram (the seeded agent is a Solution Architect) — one
        // completed task on the card, regardless of artifact count.
        // The diagram parser rejects empty-elements payloads (worthless-diagram guard),
        // so the fixture ships one minimal element.
        var diagramReply =
            "Here you go:\n```excalidraw\n{\"type\":\"excalidraw\",\"name\":\"Payments flow\"," +
            "\"elements\":[{\"type\":\"rectangle\",\"id\":\"r1\",\"x\":0,\"y\":0,\"width\":100,\"height\":50}]}\n```";
        Fake.EnqueueChunks(
            new ChatChunk(ChatChunkType.Text, diagramReply),
            new ChatChunk(ChatChunkType.Done, ""));
        (await _client.PostAsJsonAsync(
            MessagesUrl(projectId, agentId, conversationId), new SendMessageRequest("draw it")))
            .EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            (await db.ProjectAgents.AsNoTracking().FirstAsync(a => a.Id == agentId))
                .TasksCompleted.Should().Be(1);
            (await db.ArchitectureDiagrams.AsNoTracking()
                .CountAsync(d => d.ConversationId == conversationId)).Should().Be(1);
        }
    }

    [Fact]
    public async Task ConversationList_ExposesActiveRunFlag()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync();

        var gate = Fake.EnqueueGatedScript(
            before: [new ChatChunk(ChatChunkType.Text, "thinking...")],
            after: [new ChatChunk(ChatChunkType.Done, "")]);

        try
        {
            var response = await PostMessageStreamingAsync(projectId, agentId, conversationId, "hello");
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            await ReadUntilAsync(reader, "thinking...");

            var conversations = await _client.GetFromJsonAsync<List<ConversationResponse>>(
                $"/api/projects/{projectId}/agents/{agentId}/conversations", TestJsonOptions.Default);
            conversations!.Single(c => c.Id == conversationId.ToString()).HasActiveRun.Should().BeTrue();
        }
        finally
        {
            gate.TrySetResult();
        }

        await WaitForAsync<object>(async sp =>
        {
            var db = sp.GetRequiredService<AgentsDbContext>();
            var conv = await db.AgentConversations.AsNoTracking().FirstAsync(c => c.Id == conversationId);
            return conv.ActiveRunStartedAt is null ? new object() : null;
        }, "active-run marker cleared");

        var after = await _client.GetFromJsonAsync<List<ConversationResponse>>(
            $"/api/projects/{projectId}/agents/{agentId}/conversations", TestJsonOptions.Default);
        after!.Single(c => c.Id == conversationId.ToString()).HasActiveRun.Should().BeFalse();
    }
}
