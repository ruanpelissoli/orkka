namespace Orkka.IntegrationTests;

using Orkka.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

[Collection("Integration")]
public sealed class EndpointDiscoveryTests(OrkkaApiFactory factory)
{
    /// <summary>
    /// FastEndpoints only discovers endpoints from assemblies listed explicitly in
    /// AddFastEndpoints(o => o.Assemblies) — forgetting a module fails silently as 404s (and,
    /// in Orkka.Local, nothing falls through to the UI proxy: /api is host-owned). This asserts
    /// the full surface is registered. Update the floor deliberately when endpoints are
    /// added/removed, never to paper over a missing assembly.
    /// </summary>
    [Fact]
    public void Routing_ExposesFullApiSurface()
    {
        // Starting the host builds the route table.
        factory.CreateClient().Dispose();

        var paths = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .Where(p => p.TrimStart('/').StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        // The old OpenAPI-based floor was 66 paths; endpoints are counted per route template
        // here, which only raises the number. Admin-module routes are gone with the module.
        paths.Should().BeGreaterThanOrEqualTo(150,
            "a sharp drop means a module assembly is missing from AddFastEndpoints discovery");
    }
}
