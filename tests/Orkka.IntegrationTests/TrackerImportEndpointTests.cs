namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Trackers.Contracts;

/// <summary>
/// The import-by-key endpoints, wired end to end on SQLite: without a ticket source the preview
/// says so (nothing is read) and the request is refused; an unknown import id is a 404.
/// </summary>
[Collection("Integration")]
public sealed class TrackerImportEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<ProjectResponse> SeedProjectAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Import Shop", "desc"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default))!;
    }

    [Fact]
    public async Task WithoutATicketSource_ThePreviewSaysSo_AndTheImportIsRefused()
    {
        var project = await SeedProjectAsync();

        var preview = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tracker/import/preview", new { key = "SHOP-10" });
        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        var outcome = (await preview.Content.ReadFromJsonAsync<TrackerImportPreviewOutcome>(TestJsonOptions.Default))!;
        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Contain("no ticket source");

        var request = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tracker/import", new { key = "SHOP-10" });
        request.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await request.Content.ReadAsStringAsync()).Should().Contain("no ticket source");

        (await _client.GetAsync($"/api/projects/{project.Id}/tracker/import/{Ulid.NewUlid()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
