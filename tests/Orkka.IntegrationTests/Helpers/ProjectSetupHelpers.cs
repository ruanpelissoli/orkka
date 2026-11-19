namespace Orkka.IntegrationTests.Helpers;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Orkka.Modules.Projects.Features.Projects;

/// <summary>A new project's ticket source is not polled until its guided setup is started; tests
/// that exercise polling press Start the way the setup page does.</summary>
internal static class ProjectSetupHelpers
{
    public static async Task StartSetupAsync(this HttpClient client, Ulid projectId)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/setup/start", new StartProjectSetupRequest());
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
