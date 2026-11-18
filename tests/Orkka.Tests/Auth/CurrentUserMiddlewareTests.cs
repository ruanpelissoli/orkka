namespace Orkka.Tests.Auth;

using System.Security.Claims;
using Orkka.Local.Auth;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Organizations.PublicApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Contracts;
using Orkka.Tests.Caching;

public sealed class CurrentUserMiddlewareTests : IDisposable
{
    private readonly DbContextOptions<OrganizationsDbContext> _dbOptions;
    private readonly OrganizationsDbContext _db;
    private readonly OrgMembershipService _membership;
    private readonly TestCache _cache = new();

    public CurrentUserMiddlewareTests()
    {
        _dbOptions = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new OrganizationsDbContext(_dbOptions);
        _membership = new OrgMembershipService(_dbOptions);
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task InvokeAsync_UnauthenticatedRequest_PassesThrough_NoItemsSet()
    {
        var middleware = new CurrentUserMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context, _membership, _cache.Cache);

        context.Items.Should().NotContainKey(CurrentUserKeys.UserId);
        context.Items.Should().NotContainKey(CurrentUserKeys.OrgId);
        context.Items.Should().NotContainKey(CurrentUserKeys.MemberId);
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedWithOrgMember_SetsAllItems()
    {
        var orgId = Ulid.NewUlid();
        var memberId = Ulid.NewUlid();
        const string userId = "user_test_456";

        // Seed data
        _db.Organizations.Add(new Organization { Id = orgId, Name = "Test", Slug = "test" });
        _db.OrgMembers.Add(new OrgMember
        {
            Id = memberId,
            OrgId = orgId,
            UserId = userId,
            Role = OrgRole.Owner
        });
        await _db.SaveChangesAsync();

        var middleware = new CurrentUserMiddleware(_ => Task.CompletedTask);
        var context = CreateAuthenticatedContext(userId);

        await middleware.InvokeAsync(context, _membership, _cache.Cache);

        context.Items[CurrentUserKeys.UserId].Should().Be(userId);
        context.Items[CurrentUserKeys.OrgId].Should().Be(orgId);
        context.Items[CurrentUserKeys.MemberId].Should().Be(memberId);
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedWithoutOrgMember_AutoProvisionsRootUser()
    {
        const string userId = "user_no_org";

        var middleware = new CurrentUserMiddleware(_ => Task.CompletedTask);
        var context = CreateAuthenticatedContext(userId);

        await middleware.InvokeAsync(context, _membership, _cache.Cache);

        // Auto-provisions a root user with a new organization
        context.Items[CurrentUserKeys.UserId].Should().Be(userId);
        context.Items.Should().ContainKey(CurrentUserKeys.OrgId);
        context.Items.Should().ContainKey(CurrentUserKeys.MemberId);
        context.Items.Should().ContainKey(CurrentUserKeys.Role);
        context.Items[CurrentUserKeys.Role].Should().Be(OrgRole.Owner);
    }

    [Fact]
    public async Task InvokeAsync_CallsNextMiddleware()
    {
        var nextCalled = false;
        var middleware = new CurrentUserMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context, _membership, _cache.Cache);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_MemberWithGrants_LoadsOrgWidePermissionsIntoItems()
    {
        var orgId = Ulid.NewUlid();
        var memberId = Ulid.NewUlid();
        const string userId = "user_with_grants";

        _db.Organizations.Add(new Organization { Id = orgId, Name = "Test", Slug = "test-grants" });
        _db.OrgMembers.Add(new OrgMember
        {
            Id = memberId,
            OrgId = orgId,
            UserId = userId,
            Role = OrgRole.Member,
        });
        _db.MemberPermissions.AddRange(
            new MemberPermission { Id = Ulid.NewUlid(), MemberId = memberId, Permission = Permission.TasksCreate, GrantedBy = memberId, OrgId = orgId },
            new MemberPermission { Id = Ulid.NewUlid(), MemberId = memberId, Permission = Permission.AgentsChat, GrantedBy = memberId, OrgId = orgId },
            // Project-scoped grant must NOT surface in the org-wide set.
            new MemberPermission { Id = Ulid.NewUlid(), MemberId = memberId, Permission = Permission.CodeReviewMerge, ProjectId = Ulid.NewUlid(), GrantedBy = memberId, OrgId = orgId });
        await _db.SaveChangesAsync();

        var middleware = new CurrentUserMiddleware(_ => Task.CompletedTask);
        var context = CreateAuthenticatedContext(userId);

        await middleware.InvokeAsync(context, _membership, _cache.Cache);

        context.Items[CurrentUserKeys.Role].Should().Be(OrgRole.Member);
        var permissions = context.Items[CurrentUserKeys.Permissions].Should()
            .BeAssignableTo<IReadOnlySet<Permission>>().Subject;
        permissions.Should().BeEquivalentTo([Permission.TasksCreate, Permission.AgentsChat]);
    }


    [Fact]
    public async Task InvokeAsync_WorkspaceHeaderNamingAnotherOrg_Is403()
    {
        const string userId = LocalAuthHandler.LocalUserId;
        var middleware = new CurrentUserMiddleware(_ => Task.CompletedTask);
        var context = CreateAuthenticatedContext(userId);
        context.Request.Headers[CurrentUserMiddleware.WorkspaceHeader] = Ulid.NewUlid().ToString();

        await middleware.InvokeAsync(context, _membership, _cache.Cache);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Items.Should().NotContainKey(CurrentUserKeys.OrgId);
    }

    [Fact]
    public async Task InvokeAsync_MalformedWorkspaceHeader_Is400()
    {
        var middleware = new CurrentUserMiddleware(_ => Task.CompletedTask);
        var context = CreateAuthenticatedContext(LocalAuthHandler.LocalUserId);
        context.Request.Headers[CurrentUserMiddleware.WorkspaceHeader] = "not-a-ulid";

        await middleware.InvokeAsync(context, _membership, _cache.Cache);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task InvokeAsync_WorkspaceHeaderNamingOwnWorkspace_ResolvesThatWorkspace()
    {
        const string userId = LocalAuthHandler.LocalUserId;
        var first = await _membership.ResolveOrProvisionAsync(userId);
        var second = await _membership.CreateWorkspaceAsync(userId, "Second job");

        var middleware = new CurrentUserMiddleware(_ => Task.CompletedTask);
        var context = CreateAuthenticatedContext(userId);
        context.Request.Headers[CurrentUserMiddleware.WorkspaceHeader] = first.OrgId.ToString();

        await middleware.InvokeAsync(context, _membership, _cache.Cache);

        second.OrgId.Should().NotBe(first.OrgId);
        context.Items[CurrentUserKeys.OrgId].Should().Be(first.OrgId);
        context.Items[CurrentUserKeys.Role].Should().Be(OrgRole.Owner);
    }

    private static DefaultHttpContext CreateAuthenticatedContext(string userId)
    {
        var claims = new[] { new Claim("sub", userId) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var context = new DefaultHttpContext { User = principal };
        return context;
    }
}
