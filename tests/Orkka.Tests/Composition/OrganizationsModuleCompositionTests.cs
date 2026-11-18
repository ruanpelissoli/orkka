namespace Orkka.Tests.Composition;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// The tenant filter is only as good as the ICurrentUser the DbContext is built with, and
/// that wiring is invisible from the filter's own tests: ModuleDbContext takes ICurrentUser
/// as an OPTIONAL constructor parameter, so a context resolved without one silently sees
/// every org instead of failing. These tests resolve the context the way a request does —
/// through the container — and assert on what it can actually see.
/// </summary>
public sealed class OrganizationsModuleCompositionTests
{
    private sealed class StubCurrentUser(Ulid orgId, bool authenticated = true) : ICurrentUser
    {
        public bool IsAuthenticated => authenticated;
        public string UserId => "user_test";
        public Ulid OrgId => orgId;
        public Ulid MemberId => Ulid.Empty;
        public OrgRole Role => OrgRole.Owner;
        public bool IsOwner => true;
        public IReadOnlySet<Permission> Permissions => new HashSet<Permission>();
        public string? Locale => null;
        public bool HasAnyRole(params OrgRole[] roles) => roles.Contains(Role);
        public bool HasPermission(Permission permission) => true;
    }

    private static readonly Ulid OrgA = Ulid.NewUlid();
    private static readonly Ulid OrgB = Ulid.NewUlid();

    private static ServiceProvider BuildProvider(string dbName, ICurrentUser? currentUser)
    {
        var services = new ServiceCollection();
        if (currentUser is not null)
            services.AddScoped(_ => currentUser);

        // Same shape as OrganizationsModuleExtensions.AddOrganizationsPersistence: the
        // registration under test is AddDbContext, which must hand ICurrentUser to the
        // context's optional constructor parameter.
        services.AddDbContext<OrganizationsDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        return services.BuildServiceProvider();
    }

    private static async Task SeedAsync(string dbName)
    {
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using var db = new OrganizationsDbContext(options);
        db.OrgMembers.AddRange(
            new OrgMember
            {
                Id = Ulid.NewUlid(),
                OrgId = OrgA,
                UserId = "founder_of_org_a",
                Role = OrgRole.Owner,
            },
            new OrgMember
            {
                Id = Ulid.NewUlid(),
                OrgId = OrgB,
                UserId = "founder_of_org_b",
                Role = OrgRole.Owner,
            });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The one that matters: a context resolved from the container, by a caller who belongs
    /// to org A, must not see org B's founder. Every org's founder is Owner + original root,
    /// so a failure here looks exactly like "a list of strangers who are all Owners".
    /// </summary>
    [Fact]
    public async Task ResolvedDbContext_AppliesTheTenantFilter()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedAsync(dbName);

        await using var provider = BuildProvider(dbName, new StubCurrentUser(OrgA));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();

        var visible = await db.OrgMembers.Select(m => m.UserId).ToListAsync();

        visible.Should().BeEquivalentTo(["founder_of_org_a"]);
    }

    /// <summary>
    /// Pins the failure mode itself. ICurrentUser is optional, so forgetting to register it
    /// in a host does not throw — it turns every tenant-scoped read into a platform-wide one.
    /// If this ever starts failing because the parameter became required, delete it and cheer.
    /// </summary>
    [Fact]
    public async Task ResolvedDbContext_WithoutICurrentUserRegistered_SeesEveryOrg()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedAsync(dbName);

        await using var provider = BuildProvider(dbName, currentUser: null);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();

        var visible = await db.OrgMembers.Select(m => m.UserId).ToListAsync();

        visible.Should().HaveCount(2);
    }

    /// <summary>
    /// The ordering trap. ModuleDbContext snapshots IsAuthenticated in its CONSTRUCTOR, so a
    /// context built before the authenticating middleware has run is unfiltered for the rest
    /// of the request — even though the same ICurrentUser reports authenticated moments later.
    /// </summary>
    [Fact]
    public async Task ResolvedDbContext_BuiltBeforeAuthentication_SeesEveryOrg()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedAsync(dbName);

        await using var provider = BuildProvider(
            dbName, new StubCurrentUser(OrgA, authenticated: false));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();

        var visible = await db.OrgMembers.Select(m => m.UserId).ToListAsync();

        visible.Should().HaveCount(2);
    }
}
