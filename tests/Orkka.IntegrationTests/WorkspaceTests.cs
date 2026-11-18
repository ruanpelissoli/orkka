namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Organizations.Contracts;
using Orkka.Modules.Organizations.Features.Workspaces;

/// <summary>
/// One workspace (org) per job: a developer belongs to several orgs, a request names the one it
/// is about, and one without a name lands in the workspace they last switched to.
/// </summary>
[Collection("Integration")]
public sealed class WorkspaceTests(OrkkaApiFactory factory)
{
    [Fact]
    public async Task ADeveloperCanHoldSeveralWorkspaces_AndSwitchBetweenThem()
    {
        using var scope = factory.Services.CreateScope();
        var membership = scope.ServiceProvider.GetRequiredService<IOrgMembershipService>();
        var user = $"user_multi_{Ulid.NewUlid()}";

        var first = await membership.ResolveOrProvisionAsync(user);
        var acme = await membership.CreateWorkspaceAsync(user, "Acme (day job)");
        var contoso = await membership.CreateWorkspaceAsync(user, "Contoso (contract)");

        var workspaces = await membership.ListWorkspacesAsync(user);
        workspaces.Select(w => w.OrgId).Should().BeEquivalentTo([first.OrgId, acme.OrgId, contoso.OrgId],
            "the old one-org-per-user constraint is gone");
        workspaces.Should().ContainSingle(w => w.IsActive).Which.OrgId.Should().Be(contoso.OrgId,
            "a new workspace is the active one");
        workspaces.Single(w => w.OrgId == acme.OrgId).Role.Should().Be(OrgRole.Owner);
        workspaces.Single(w => w.OrgId == acme.OrgId).Name.Should().Be("Acme (day job)");

        (await membership.ResolveAsync(user))!.OrgId.Should().Be(contoso.OrgId);

        (await membership.SetActiveWorkspaceAsync(user, acme.OrgId)).Should().BeTrue();
        (await membership.ResolveAsync(user))!.OrgId.Should().Be(acme.OrgId);
        (await membership.GetOrgIdForUserAsync(user)).Should().Be(acme.OrgId,
            "both resolution paths agree on the active workspace");

        (await membership.ResolveInOrgAsync(user, contoso.OrgId))!.OrgId.Should().Be(contoso.OrgId,
            "a named workspace wins over the active one");
    }

    [Fact]
    public async Task AWorkspaceTheUserIsNotIn_ResolvesToNothing_AndCannotBeActivated()
    {
        using var scope = factory.Services.CreateScope();
        var membership = scope.ServiceProvider.GetRequiredService<IOrgMembershipService>();
        var dana = $"user_dana_{Ulid.NewUlid()}";
        var sam = $"user_sam_{Ulid.NewUlid()}";
        await membership.ResolveOrProvisionAsync(dana);
        var samsOrg = await membership.ResolveOrProvisionAsync(sam);

        (await membership.ResolveInOrgAsync(dana, samsOrg.OrgId)).Should().BeNull();
        (await membership.SetActiveWorkspaceAsync(dana, samsOrg.OrgId)).Should().BeFalse();
    }

    [Fact]
    public async Task TheWorkspaceEndpoints_ListCreateAndSwitch()
    {
        var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest("Side gig"));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var workspace = (await created.Content.ReadFromJsonAsync<WorkspaceResponse>(TestJsonOptions.Default))!;
        workspace.Name.Should().Be("Side gig");
        workspace.Role.Should().Be("Owner");

        var list = await client.GetFromJsonAsync<WorkspaceResponse[]>("/api/me/workspaces", TestJsonOptions.Default);
        list!.Select(w => w.OrgId).Should().Contain([workspace.OrgId, TestCurrentUser.TestOrgId]);

        (await client.PostAsJsonAsync("/api/me/active-workspace", new SetActiveWorkspaceRequest(TestCurrentUser.TestOrgId)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync("/api/me/active-workspace", new SetActiveWorkspaceRequest(Ulid.NewUlid())))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest("")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

}
