namespace Orkka.Tests.Architecture;

using System.Reflection;
using FluentAssertions;
using Orkka.SharedKernel.Caching;

/// <summary>
/// Guards the one cache invariant that cannot fail loudly: tenant scoping.
/// </summary>
/// <remarks>
/// <c>ModuleDbContext</c> filters every read by <c>OrgId</c>, but a cache hit skips the query
/// entirely — so an un-scoped key serves one tenant's data to another with nothing left to
/// stop it. There is no exception, no failing test and no log line when that happens; the
/// only defence is that keys are impossible to build wrong, which is what these tests check.
/// </remarks>
public sealed class CacheKeyConventionTests
{
    /// <summary>
    /// Keys that are cross-org on purpose. Adding to this list is a deliberate decision that
    /// the data is identical for every tenant — not a way to silence the test.
    /// </summary>
    private const string PlatformWidePrefix = "platform:";

    [Fact]
    public void EveryParameterlessKey_IsExplicitlyPlatformWide()
    {
        var properties = typeof(CacheKeys)
            .GetProperties(BindingFlags.Public | BindingFlags.Static);

        properties.Should().NotBeEmpty();

        foreach (var property in properties)
        {
            var key = (string)property.GetValue(null)!;

            key.Should().StartWith(
                PlatformWidePrefix,
                "'{0}' takes no scope parameter, so it can only be safe if its data is the " +
                "same for every org — which the '{1}' prefix is how we declare",
                property.Name, PlatformWidePrefix);
        }
    }

    [Fact]
    public void EveryKeyBuilder_TakesAnOrgOrUserScope()
    {
        var methods = typeof(CacheKeys)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)   // property getters are covered above
            .ToList();

        methods.Should().NotBeEmpty();

        foreach (var method in methods)
        {
            var parameterNames = method.GetParameters().Select(p => p.Name).ToList();

            parameterNames.Should().Contain(
                n => n == "orgId" || n == "userId",
                "'{0}' builds a cache key and must be scoped: 'orgId' for tenant data, or " +
                "'userId' for per-identity data that resolves the org. Genuinely " +
                "cross-org data belongs behind a parameterless '{1}' key instead",
                method.Name, PlatformWidePrefix);
        }
    }

    [Fact]
    public void TenantScopedKeys_PutTheScopeFirst()
    {
        // Not cosmetic: it makes a call site that passes arguments in the wrong order fail to
        // compile (Ulid orgId vs string slug) rather than silently produce a valid-looking key.
        var methods = typeof(CacheKeys)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.GetParameters().Any(p => p.Name == "orgId"));

        foreach (var method in methods)
        {
            method.GetParameters()[0].Name.Should().Be(
                "orgId", "'{0}' is tenant-scoped, so orgId is its first parameter", method.Name);
        }
    }

    [Fact]
    public void DistinctInputs_ProduceDistinctKeys()
    {
        var orgA = Ulid.NewUlid();
        var orgB = Ulid.NewUlid();
        var projectA = Ulid.NewUlid();
        var projectB = Ulid.NewUlid();

        var keys = new[]
        {
            CacheKeys.Membership("user_a"),
            CacheKeys.Membership("user_b"),
            CacheKeys.Membership("user_a", orgA),
            CacheKeys.Membership("user_a", orgB),
            CacheKeys.ModelCatalog,
            CacheKeys.ProjectSummary(orgA, projectA),
            CacheKeys.ProjectSummary(orgA, projectB),
            // Same project id under a different org must never collide — this is the exact
            // shape a cross-tenant leak would take.
            CacheKeys.ProjectSummary(orgB, projectA),
            CacheKeys.ProjectRepos(orgA, projectA),
            CacheKeys.ProjectSlug(orgA, "my-project"),
            CacheKeys.ProjectSlug(orgB, "my-project"),
        };

        keys.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("projects:summary:01ABC:01DEF", "projects.summary")]
    [InlineData("auth:member:user_123", "auth.member")]
    [InlineData("platform:admins", "platform.admins")]
    [InlineData("nocolons", "nocolons")]
    public void CacheFamily_KeepsTagCardinalityBounded(string key, string expectedFamily)
    {
        // Identifiers past the second segment must never reach a metric tag — one series per
        // org or per project would take the metrics backend down.
        CacheFamily.Of(key).Should().Be(expectedFamily);
    }
}
