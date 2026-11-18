namespace Orkka.IntegrationTests;

using System.Net;
using Orkka.IntegrationTests.Fixtures;
using FluentAssertions;

[Collection("Integration")]
public sealed class HealthEndpointTests(OrkkaApiFactory factory)
{
    [Fact]
    public async Task GetHealth_ReturnsOkWithHealthyStatus()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().ContainEquivalentOf("healthy"); // MapHealthChecks default writer returns "Healthy"
    }
}
