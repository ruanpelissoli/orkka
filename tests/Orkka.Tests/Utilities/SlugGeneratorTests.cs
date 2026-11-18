namespace Orkka.Tests.Utilities;

using Orkka.SharedKernel.Utilities;
using FluentAssertions;

public sealed class SlugGeneratorTests
{
    [Theory]
    [InlineData("My SaaS", "my-saas")]
    [InlineData("BarbecueParty", "barbecueparty")]
    [InlineData("plant me", "plant-me")]
    [InlineData("  Weird -- Name!! ", "weird-name")]
    [InlineData("Ação São Paulo", "a-o-s-o-paulo")]
    public void GenerateProjectSlug_SlugifiesNames(string name, string expected)
    {
        SlugGenerator.GenerateProjectSlug(name).Should().Be(expected);
    }

    [Fact]
    public void GenerateProjectSlug_EmptyName_FallsBack()
    {
        SlugGenerator.GenerateProjectSlug("!!!").Should().Be("project");
    }

    [Theory]
    [InlineData("brief")]
    public void GenerateProjectSlug_ReservedRouteLiterals_GetSuffixed(string reserved)
    {
        SlugGenerator.GenerateProjectSlug(reserved).Should().Be($"{reserved}-1");
    }

    [Fact]
    public void GenerateManagedRepoName_AppendsFullProjectIdSuffix()
    {
        var projectId = Ulid.Parse("01JABCDEFGHJKMNPQRSTVWXYZ0");

        var name = SlugGenerator.GenerateManagedRepoName("My Todo App", projectId);

        name.Should().Be("my-todo-app-01jabcdefghjkmnpqrstvwxyz0");
    }

    [Fact]
    public void GenerateManagedRepoName_SameNameSameMillisecond_NeverCollides()
    {
        // ULIDs share their timestamp prefix within a millisecond — only the full id
        // guarantees uniqueness, which is what makes the no-precheck design safe.
        var a = SlugGenerator.GenerateManagedRepoName("todo-app", Ulid.NewUlid());
        var b = SlugGenerator.GenerateManagedRepoName("todo-app", Ulid.NewUlid());

        a.Should().NotBe(b);
    }

    [Fact]
    public void GenerateManagedRepoName_EmptySlug_FallsBack()
    {
        var projectId = Ulid.NewUlid();

        var name = SlugGenerator.GenerateManagedRepoName("!!!", projectId);

        name.Should().Be($"app-{projectId.ToString().ToLowerInvariant()}");
    }

    [Fact]
    public void GenerateManagedRepoName_LongName_TruncatesButKeepsSuffix()
    {
        var projectId = Ulid.NewUlid();
        var longName = string.Join(" ", Enumerable.Repeat("verylongword", 20));

        var name = SlugGenerator.GenerateManagedRepoName(longName, projectId);

        name.Length.Should().BeLessThanOrEqualTo(100);
        name.Should().EndWith(projectId.ToString().ToLowerInvariant());
    }

    [Fact]
    public void GenerateAppHostLabel_UsesTheIdTail_NotItsTimestampHead()
    {
        var projectId = Ulid.Parse("01JABCDEFGHJKMNPQRSTVWXYZ0");

        var label = SlugGenerator.GenerateAppHostLabel("My Todo App", projectId);

        // "stvwxyz0" is the id's tail; "01jabcde" — its timestamp head — is what
        // GenerateSlug uses and what would collide within a millisecond.
        label.Should().Be("my-todo-app-stvwxyz0");
    }

    [Theory]
    [InlineData("My SaaS")]
    [InlineData("  Weird -- Name!! ")]
    [InlineData("Ação São Paulo")]
    [InlineData("!!!")]
    [InlineData("x")]
    public void GenerateAppHostLabel_IsAlwaysAValidDnsLabel(string name)
    {
        var label = SlugGenerator.GenerateAppHostLabel(name, Ulid.NewUlid());

        label.Length.Should().BeLessThanOrEqualTo(63);
        // Lowercase alphanumerics and single interior hyphens only — a hostname that Vercel
        // rejects would silently cost the project its platform URL.
        label.Should().MatchRegex("^[a-z0-9]+(-[a-z0-9]+)*$");
    }

    [Fact]
    public void GenerateAppHostLabel_EmptySlug_FallsBack()
    {
        var projectId = Ulid.NewUlid();

        var label = SlugGenerator.GenerateAppHostLabel("!!!", projectId);

        label.Should().Be($"app-{projectId.ToString()[^8..].ToLowerInvariant()}");
    }

    [Fact]
    public void GenerateAppHostLabel_LongName_TruncatesButKeepsSuffix()
    {
        var projectId = Ulid.NewUlid();
        var longName = string.Join(" ", Enumerable.Repeat("verylongword", 20));

        var label = SlugGenerator.GenerateAppHostLabel(longName, projectId);

        label.Length.Should().BeLessThanOrEqualTo(63);
        label.Should().EndWith($"-{projectId.ToString()[^8..].ToLowerInvariant()}");
    }

    [Fact]
    public void GenerateAppHostLabel_SameNameSameMillisecond_NeverCollides()
    {
        // Hostnames are global across tenants: a collision would point one org's domain at
        // another org's app. ULIDs minted in the same millisecond differ only in their tail.
        var a = SlugGenerator.GenerateAppHostLabel("todo-app", Ulid.NewUlid());
        var b = SlugGenerator.GenerateAppHostLabel("todo-app", Ulid.NewUlid());

        a.Should().NotBe(b);
    }

    [Theory]
    [InlineData("My SaaS", "MS")]
    [InlineData("Barbecue Party App", "BPA")]
    [InlineData("Lootstash", "LOO")]
    [InlineData("one two three four five six", "OTTFF")]
    [InlineData("2Fast 4You", "FY")]
    public void GenerateProjectKey_UsesInitialsOrPrefix(string name, string expected)
    {
        SlugGenerator.GenerateProjectKey(name).Should().Be(expected);
    }

    [Fact]
    public void GenerateProjectKey_NoLetters_FallsBack()
    {
        SlugGenerator.GenerateProjectKey("123 456").Should().Be("PRJ");
    }

    [Theory]
    [InlineData("My SaaS")]
    [InlineData("x")]
    [InlineData("Ünïcode Nàme")]
    [InlineData("a-b-c-d-e-f-g")]
    public void GenerateProjectKey_AlwaysMatchesContract(string name)
    {
        SlugGenerator.GenerateProjectKey(name).Should().MatchRegex("^[A-Z][A-Z0-9]{1,4}$");
    }
}
