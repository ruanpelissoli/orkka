namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Organizations.Features.Members;
using Orkka.Modules.Organizations.Features.Settings;

[Collection("Integration")]
public sealed class OrgProfileEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    // The factory seeds this and the whole collection shares it — every test restores it.
    private const string SeededName = "Test Organization";

    private Task RestoreAsync() =>
        _client.PutAsJsonAsync("/api/org", new UpdateOrgProfileRequest(SeededName));

    [Fact]
    public async Task Rename_PersistsAndSurfacesOnMe()
    {
        try
        {
            var response = await _client.PutAsJsonAsync(
                "/api/org", new UpdateOrgProfileRequest("  Acme   Rockets  "));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var profile = await response.Content
                .ReadFromJsonAsync<OrgProfileResponse>(TestJsonOptions.Default);
            profile!.Name.Should().Be("Acme Rockets", "the name is trimmed and collapsed");

            var me = await _client.GetFromJsonAsync<MeResponse>("/api/org/me", TestJsonOptions.Default);
            me!.OrgName.Should().Be("Acme Rockets");
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_RejectsEmptyNames(string name)
    {
        var response = await _client.PutAsJsonAsync("/api/org", new UpdateOrgProfileRequest(name));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var me = await _client.GetFromJsonAsync<MeResponse>("/api/org/me", TestJsonOptions.Default);
        me!.OrgName.Should().Be(SeededName, "a rejected rename must not touch the stored name");
    }

    [Fact]
    public async Task Rename_RejectsNamesLongerThanTheColumn()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/org", new UpdateOrgProfileRequest(new string('a', OrgNaming.MaxLength + 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rename_IsForbiddenForNonOwners()
    {
        TestCurrentUser.CurrentRole = OrgRole.Member;
        TestCurrentUser.CurrentPermissions = new HashSet<Permission> { Permission.ProjectsManage };
        try
        {
            var response = await _client.PutAsJsonAsync(
                "/api/org", new UpdateOrgProfileRequest("Member Renamed Us"));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the org's identity is an ownership concern, not a grant");
        }
        finally
        {
            TestCurrentUser.Reset();
            await RestoreAsync();
        }
    }
}
