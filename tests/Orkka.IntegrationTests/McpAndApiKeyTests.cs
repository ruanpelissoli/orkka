namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Organizations.Contracts;
using Orkka.Modules.Organizations.Features.ApiKeys;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Org API keys (mint / list / revoke, anti-escalation, validator semantics) and the MCP
/// endpoint (tools/list gated by permission, board operations through tools/call). The test
/// fixture authenticates every request as the test owner, so the API-key HTTP handler itself is
/// exercised through the validator it delegates to.
/// </summary>
[Collection("Integration")]
public sealed class McpAndApiKeyTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    // ── API keys ────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiKey_CreateListRevoke_AndValidatorSemantics()
    {
        var create = await _client.PostAsJsonAsync("/api/me/api-keys",
            new CreateApiKeyRequest("Claude Code", ["TasksCreate", "PlansReview"]));
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await create.Content.ReadFromJsonAsync<CreateApiKeyResponse>(TestJsonOptions.Default);
        created!.RawKey.Should().StartWith("orkka_key_");
        created.Key.Prefix.Should().Be(created.RawKey[..16]);
        created.Key.Permissions.Should().BeEquivalentTo(["PlansReview", "TasksCreate"]);

        var list = await _client.GetFromJsonAsync<List<ApiKeyResponse>>("/api/me/api-keys", TestJsonOptions.Default);
        list.Should().Contain(k => k.Id == created.Key.Id && !k.Revoked);

        // Validator: resolves to the creator with the requested permissions (owner → as requested).
        using (var scope = factory.Services.CreateScope())
        {
            var validator = scope.ServiceProvider.GetRequiredService<IOrgApiKeyValidator>();
            var principal = await validator.ValidateAsync(created.RawKey);
            principal.Should().NotBeNull();
            principal!.OrgId.Should().Be(TestCurrentUser.TestOrgId);
            principal.UserId.Should().Be(TestCurrentUser.TestUserId);
            principal.Permissions.Should().BeEquivalentTo([Permission.TasksCreate, Permission.PlansReview]);
            (await validator.ValidateAsync("orkka_key_definitely-not-a-real-key-value-here")).Should().BeNull();
            (await validator.ValidateAsync("orkr_something")).Should().BeNull();
        }

        var revoke = await _client.DeleteAsync($"/api/me/api-keys/{created.Key.Id}");
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using (var scope = factory.Services.CreateScope())
        {
            var validator = scope.ServiceProvider.GetRequiredService<IOrgApiKeyValidator>();
            (await validator.ValidateAsync(created.RawKey)).Should().BeNull("revoked keys must stop working immediately");
        }
        var after = await _client.GetFromJsonAsync<List<ApiKeyResponse>>("/api/me/api-keys", TestJsonOptions.Default);
        after.Should().Contain(k => k.Id == created.Key.Id && k.Revoked);
    }

    [Fact]
    public async Task ApiKey_RejectsUnknownPermission_EmptySet_AndUnknownProject()
    {
        (await _client.PostAsJsonAsync("/api/me/api-keys", new CreateApiKeyRequest("x", ["NotAPermission"])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync("/api/me/api-keys", new CreateApiKeyRequest("x", [])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync("/api/me/api-keys", new CreateApiKeyRequest("x", ["TasksCreate"], ProjectId: "no-such-project")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ApiKey_ProjectBound_CarriesTheProject()
    {
        var project = await CreateProjectAsync("Key Bound");
        var create = await _client.PostAsJsonAsync("/api/me/api-keys",
            new CreateApiKeyRequest("bound", ["TasksCreate"], ProjectId: project.Slug));
        var created = await create.Content.ReadFromJsonAsync<CreateApiKeyResponse>(TestJsonOptions.Default);
        created!.Key.ProjectId.Should().Be(project.Id);

        using var scope = factory.Services.CreateScope();
        var principal = await scope.ServiceProvider.GetRequiredService<IOrgApiKeyValidator>().ValidateAsync(created.RawKey);
        principal!.ProjectId.Should().Be(project.Id);
    }

    // ── MCP ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mcp_ToolsList_ReturnsTheOrkkaSurface()
    {
        var result = await McpCallAsync("tools/list", new { });
        var tools = result.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        // Owner (test user) sees every tool, reads and writes.
        tools.Should().Contain(["list_projects", "list_tasks", "get_task", "get_inbox", "create_tasks", "submit_task",
            "approve_plan", "reject_plan", "review_pull_request", "merge_pull_request", "start_autopilot", "chat_with_agent"]);
        // Every schema is an object schema (the SDK validates this too).
        foreach (var t in result.GetProperty("tools").EnumerateArray())
            t.GetProperty("inputSchema").GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public async Task Mcp_ReadTools_SeeTheProject_AndCreateTasks_LandsInDrafting()
    {
        var project = await CreateProjectAsync("MCP Reads", withRepo: true);

        var projects = await McpToolAsync("list_projects", new { });
        projects.Should().Contain(project.Slug);

        var created = await McpToolAsync("create_tasks", new
        {
            project = project.Slug,
            tasks = new[] { new { title = "Add health endpoint", description = "GET /health", priority = "High", type = "Feature" } },
        });
        created.Should().Contain("Add health endpoint").And.Contain("task_id:");

        var listed = await McpToolAsync("list_tasks", new { project = project.Slug, lane = "Drafting" });
        listed.Should().Contain("Add health endpoint");

        var inbox = await McpToolAsync("get_inbox", new { project = project.Slug });
        inbox.Should().Contain("Nothing is waiting", "a Drafting task is not waiting on a human");
    }

    [Fact]
    public async Task Mcp_ApprovePlan_MovesTheTaskToDeveloping()
    {
        var project = await CreateProjectAsync("MCP Plan", withRepo: true);
        var orgId = TestCurrentUser.TestOrgId;

        // Seed a task parked on plan review with a PendingReview plan.
        Ulid taskId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var repoId = (await _client.GetFromJsonAsync<List<ProjectRepoResponse>>($"/api/projects/{project.Id}/repos", TestJsonOptions.Default))![0].Id;
            var task = new TaskEntity
            {
                Id = Ulid.NewUlid(), OrgId = orgId, ProjectId = project.Id, RepoId = repoId, Number = 4242,
                Title = "Plan me", CurrentLane = Lane.PlanReady, Status = TaskStatus.WaitingPlanApproval,
            };
            db.Tasks.Add(task);
            db.TaskPlans.Add(new TaskPlan
            {
                Id = Ulid.NewUlid(), OrgId = orgId, TaskId = task.Id, Version = 1, Status = PlanStatus.PendingReview,
                Content = new PlanContent { Summary = "Do the thing", Approach = "Carefully", FilesChanged = [] },
            });
            await db.SaveChangesAsync();
            taskId = task.Id;
        }

        var inbox = await McpToolAsync("get_inbox", new { project = project.Slug });
        inbox.Should().Contain("Plan me").And.Contain("WaitingPlanApproval");

        var detail = await McpToolAsync("get_task", new { project = project.Slug, task_id = taskId.ToString() });
        detail.Should().Contain("awaiting your review").And.Contain("Do the thing");

        var approved = await McpToolAsync("approve_plan", new { task_id = taskId.ToString() });
        approved.Should().Contain("approved");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var task = await db.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == taskId);
            task.CurrentLane.Should().Be(Lane.Developing);
            task.Status.Should().Be(TaskStatus.InQueue);
        }

        // Nothing left to approve → a clear tool error, not an exception.
        var again = await McpCallAsync("tools/call", new { name = "approve_plan", arguments = new { task_id = taskId.ToString() } });
        again.GetProperty("isError").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Mcp_UnknownTool_IsAToolError_NotA500()
    {
        var result = await McpCallAsync("tools/call", new { name = "nope", arguments = new { } });
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private sealed record CreatedProject(Ulid Id, string Slug);

    private async Task<CreatedProject> CreateProjectAsync(string name, bool withRepo = false)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, "desc"));
        var project = await resp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        if (withRepo)
            await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
                new AddProjectRepoRequest("mcp-repo", $"https://github.com/acme/{project.Slug}.git", "main", null, null));
        return new CreatedProject(project!.Id, project.Slug);
    }

    /// <summary>tools/call → the tool's text; fails the test on isError.</summary>
    private async Task<string> McpToolAsync(string tool, object arguments)
    {
        var result = await McpCallAsync("tools/call", new { name = tool, arguments });
        var text = string.Join("\n", result.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("text").GetString()));
        var isError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
        isError.Should().BeFalse($"tool {tool} returned an error: {text}");
        return text;
    }

    /// <summary>One JSON-RPC request to /mcp (stateless Streamable HTTP); returns the "result".</summary>
    private async Task<JsonElement> McpCallAsync(string method, object @params)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await _client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);

        // The transport may answer as JSON or as an SSE stream with one data: line.
        var json = raw.TrimStart().StartsWith('{')
            ? raw
            : raw.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))["data:".Length..].Trim();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"MCP error: {error}");
        return root.GetProperty("result").Clone();
    }
}
