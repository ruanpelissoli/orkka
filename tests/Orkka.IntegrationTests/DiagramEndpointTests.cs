namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.Diagrams;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Features.Projects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Exercises the diagram update endpoint's request validation. UpdateDiagramValidator must
/// derive from FastEndpoints.Validator (not FluentValidation.AbstractValidator) or it is
/// never discovered and these limits are silently unenforced.
/// </summary>
[Collection("Integration")]
public sealed class DiagramEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, Ulid DiagramId)> SeedDiagramAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest($"Diagram Validation {Ulid.NewUlid()}", "desc"));
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
        var diagram = new ArchitectureDiagram
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            ProjectId = project.Id,
            AgentId = agent.Id,
            ConversationId = conversation.Id,
            Title = "Initial diagram",
            Type = DiagramType.SystemArchitecture,
        };
        db.ProjectAgents.Add(agent);
        db.AgentConversations.Add(conversation);
        db.ArchitectureDiagrams.Add(diagram);
        await db.SaveChangesAsync();

        return (project.Id, diagram.Id);
    }

    private Task<HttpResponseMessage> UpdateAsync(Ulid projectId, Ulid diagramId, UpdateDiagramRequest request) =>
        _client.PutAsJsonAsync($"/api/projects/{projectId}/diagrams/{diagramId}", request);

    [Fact]
    public async Task Update_TitleOverMaxLength_ReturnsBadRequest()
    {
        var (projectId, diagramId) = await SeedDiagramAsync();

        var response = await UpdateAsync(projectId, diagramId,
            new UpdateDiagramRequest(new string('x', 501), null, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_DescriptionOverMaxLength_ReturnsBadRequest()
    {
        var (projectId, diagramId) = await SeedDiagramAsync();

        var response = await UpdateAsync(projectId, diagramId,
            new UpdateDiagramRequest(null, null, new string('x', 5001)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_DiagramDataNotAnObject_ReturnsBadRequest()
    {
        var (projectId, diagramId) = await SeedDiagramAsync();
        var array = JsonSerializer.Deserialize<JsonElement>("[1,2,3]");

        var response = await UpdateAsync(projectId, diagramId,
            new UpdateDiagramRequest(null, array, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_DiagramDataOverFiveMegabytes_ReturnsBadRequest()
    {
        var (projectId, diagramId) = await SeedDiagramAsync();
        var oversized = JsonSerializer.Deserialize<JsonElement>(
            $$"""{"blob":"{{new string('x', 5 * 1024 * 1024)}}"}""");

        var response = await UpdateAsync(projectId, diagramId,
            new UpdateDiagramRequest(null, oversized, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ValidRequest_Succeeds()
    {
        var (projectId, diagramId) = await SeedDiagramAsync();
        var data = JsonSerializer.Deserialize<JsonElement>("""{"elements":[]}""");

        var response = await UpdateAsync(projectId, diagramId,
            new UpdateDiagramRequest("Renamed diagram", data, "Updated description"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DiagramResponse>(TestJsonOptions.Default);
        body!.Title.Should().Be("Renamed diagram");
        body.Version.Should().Be(2);
    }
}
