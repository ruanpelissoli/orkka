namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Organizations.Features.Members;
using FluentAssertions;

[Collection("Integration")]
public sealed class OrgMeEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetMe_ReturnsRoleAndMember()
    {
        var response = await _client.GetAsync("/api/org/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync<MeResponse>(TestJsonOptions.Default);
        me!.Role.Should().Be("Owner");
        me.MemberId.Should().Be(TestCurrentUser.TestMemberId.ToString());
    }

    [Fact]
    public async Task Preferences_DefaultOff_RoundTripsThroughUpdate()
    {
        var initial = await _client.GetFromJsonAsync<MeResponse>("/api/org/me", TestJsonOptions.Default);
        initial!.Preferences.ShowTechnicalDetails.Should().BeFalse(
            "a member with no stored preferences must default to simple mode");

        var updateResponse = await _client.PutAsJsonAsync(
            "/api/org/me/preferences", new UpdateMyPreferencesRequest(ShowTechnicalDetails: true));
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await updateResponse.Content
            .ReadFromJsonAsync<MemberPreferences>(TestJsonOptions.Default);
        updated!.ShowTechnicalDetails.Should().BeTrue();

        try
        {
            var me = await _client.GetFromJsonAsync<MeResponse>("/api/org/me", TestJsonOptions.Default);
            me!.Preferences.ShowTechnicalDetails.Should().BeTrue("the preference must persist");
        }
        finally
        {
            // Shared factory/member across the collection — restore the default.
            await _client.PutAsJsonAsync("/api/org/me/preferences", new UpdateMyPreferencesRequest(ShowTechnicalDetails: false));
        }
    }

    [Fact]
    public async Task Preferences_PartialUpdate_LeavesOtherFieldsAlone()
    {
        // The tour flag and the technical-details toggle are flipped by different screens,
        // seconds apart. A whole-object PUT would make each one silently reset the other.
        await _client.PutAsJsonAsync(
            "/api/org/me/preferences", new UpdateMyPreferencesRequest(ShowTechnicalDetails: true));
        try
        {
            var response = await _client.PutAsJsonAsync(
                "/api/org/me/preferences", new UpdateMyPreferencesRequest(OnboardingTourSeen: true));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var updated = await response.Content
                .ReadFromJsonAsync<MemberPreferences>(TestJsonOptions.Default);
            updated!.OnboardingTourSeen.Should().BeTrue();
            updated.ShowTechnicalDetails.Should().BeTrue(
                "an omitted field must keep its stored value");

            var me = await _client.GetFromJsonAsync<MeResponse>("/api/org/me", TestJsonOptions.Default);
            me!.Preferences.OnboardingTourSeen.Should().BeTrue();
            me.Preferences.ShowTechnicalDetails.Should().BeTrue();
        }
        finally
        {
            // Shared factory/member across the collection — restore the defaults.
            await _client.PutAsJsonAsync(
                "/api/org/me/preferences",
                new UpdateMyPreferencesRequest(ShowTechnicalDetails: false, OnboardingTourSeen: false));
        }
    }
}
