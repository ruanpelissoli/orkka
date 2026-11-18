namespace Orkka.Tests.Features.Organizations;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// The org roster is the one read where a tenant-filter failure is directly visible to a
/// customer: /api/org/members would answer with every member of every org on the platform.
/// The filter lambda closes over the DbContext that BUILT the model, and EF caches that model
/// for the lifetime of the provider — so the invariant worth pinning is not "the filter
/// exists" but "a second context reusing the cached model still filters to ITS OWN org".
/// </summary>
public sealed class OrgMemberTenantIsolationTests
{
    private sealed class FakeCurrentUser(Ulid orgId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
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

    /// <summary>One options instance for every context here, so they share a provider —
    /// and therefore the cached model — exactly as a running host does.</summary>
    private static DbContextOptions<OrganizationsDbContext> SharedOptions() =>
        new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static async Task SeedTwoOrgsAsync(DbContextOptions<OrganizationsDbContext> options)
    {
        // Seeded through the no-tenant shape (the Worker/Migrator path), which is the only
        // way to write rows into two orgs at once.
        await using var seed = new OrganizationsDbContext(options);
        // Ids assigned here, not left to SaveChanges: the change tracker keys on Id at Add
        // time, so three Ulid.Empty rows collide before stamping ever runs.
        seed.OrgMembers.AddRange(
            new OrgMember { Id = Ulid.NewUlid(), OrgId = OrgA, UserId = "user_in_org_a", Role = OrgRole.Owner },
            new OrgMember { Id = Ulid.NewUlid(), OrgId = OrgB, UserId = "user_in_org_b", Role = OrgRole.Owner },
            new OrgMember { Id = Ulid.NewUlid(), OrgId = OrgB, UserId = "user_also_in_org_b", Role = OrgRole.Member });
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task OrgMembers_AreScopedToTheCallersOrg()
    {
        var options = SharedOptions();
        await SeedTwoOrgsAsync(options);

        await using var asOrgA = new OrganizationsDbContext(options, new FakeCurrentUser(OrgA));

        var visible = await asOrgA.OrgMembers.Select(m => m.UserId).ToListAsync();

        visible.Should().BeEquivalentTo(["user_in_org_a"]);
    }

    [Fact]
    public async Task OrgMembers_StayScoped_WhenASecondOrgReusesTheCachedModel()
    {
        var options = SharedOptions();
        await SeedTwoOrgsAsync(options);

        // First tenant-scoped context builds the model; its `this` is what the filter closes over.
        await using (var asOrgA = new OrganizationsDbContext(options, new FakeCurrentUser(OrgA)))
        {
            (await asOrgA.OrgMembers.CountAsync()).Should().Be(1);
        }

        // Second context reuses that cached model. If the filter had baked in org A's id
        // rather than re-reading it per query, this would come back empty — or worse, with
        // org A's roster.
        await using var asOrgB = new OrganizationsDbContext(options, new FakeCurrentUser(OrgB));

        var visible = await asOrgB.OrgMembers.Select(m => m.UserId).ToListAsync();

        visible.Should().BeEquivalentTo(["user_in_org_b", "user_also_in_org_b"]);
    }

    /// <summary>
    /// The deliberate escape hatch, pinned so it stays deliberate: with no ICurrentUser the
    /// filter is bypassed entirely. Every caller constructing a context this way (membership
    /// resolution, invite acceptance, the Worker) is trusting itself to scope its own query.
    /// </summary>
    [Fact]
    public async Task OrgMembers_AreUnfiltered_WithoutACurrentUser()
    {
        var options = SharedOptions();
        await SeedTwoOrgsAsync(options);

        await using var noTenant = new OrganizationsDbContext(options);

        (await noTenant.OrgMembers.CountAsync()).Should().Be(3);
    }
    /// <summary>
    /// Production's actual startup ordering. Hosted services (the platform-admin seeder,
    /// event handlers) run before the first HTTP request and use a context with NO
    /// ICurrentUser, so THAT context builds and caches the model — and compiles the first
    /// query against a filter whose _hasTenant is false. If EF folded that in rather than
    /// parameterising it, every tenant-scoped read afterwards would be platform-wide for the
    /// life of the process, which is exactly what a roster full of other orgs' owners is.
    /// </summary>
    [Fact]
    public async Task OrgMembers_StayScoped_WhenANoTenantContextCompiledTheQueryFirst()
    {
        var options = SharedOptions();
        await SeedTwoOrgsAsync(options);

        // Warm the model AND the compiled query with the unfiltered shape.
        await using (var noTenant = new OrganizationsDbContext(options))
        {
            (await noTenant.OrgMembers.Select(m => m.UserId).ToListAsync())
                .Should().HaveCount(3);
        }

        await using var asOrgA = new OrganizationsDbContext(options, new FakeCurrentUser(OrgA));

        var visible = await asOrgA.OrgMembers.Select(m => m.UserId).ToListAsync();

        visible.Should().BeEquivalentTo(["user_in_org_a"]);
    }
    /// <summary>
    /// Defence in depth, pinned. The endpoints scope by OrgId themselves, so their queries
    /// must still return only the caller's org when the global filter contributes nothing —
    /// which is the state a context built without a resolved caller is already in.
    ///
    /// This mirrors the query in ListMembersEndpoint. If that endpoint ever drops its own
    /// Where clause and goes back to trusting the filter alone, this test keeps passing and
    /// tells you nothing — so keep them in step.
    /// </summary>
    [Fact]
    public async Task ExplicitOrgScoping_HoldsEvenWithTheGlobalFilterInert()
    {
        var options = SharedOptions();
        await SeedTwoOrgsAsync(options);

        // No ICurrentUser: the filter is inert, exactly as in the failure mode.
        await using var unfiltered = new OrganizationsDbContext(options);

        var visible = await unfiltered.OrgMembers
            .AsNoTracking()
            .Where(m => m.OrgId == OrgA)
            .Select(m => m.UserId)
            .ToListAsync();

        visible.Should().BeEquivalentTo(["user_in_org_a"]);
    }
}


