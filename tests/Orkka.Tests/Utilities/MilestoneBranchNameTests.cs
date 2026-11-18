namespace Orkka.Tests.Utilities;

using Orkka.SharedKernel.Utilities;
using FluentAssertions;

/// <summary>
/// A milestone's integration branch is what every task under it forks from and merges into, so
/// two milestones sharing a name would silently merge one's work into the other's ship.
/// </summary>
public sealed class MilestoneBranchNameTests
{
    [Theory]
    [InlineData("Checkout flow", "orkka/milestone/checkout-flow-")]
    [InlineData("Payment Webhooks", "orkka/milestone/payment-webhooks-")]
    [InlineData("  Weird -- Name!! ", "orkka/milestone/weird-name-")]
    public void NamesAreSlugifiedAndPrefixed(string title, string expectedPrefix)
    {
        SlugGenerator.GenerateMilestoneBranchName(title, Ulid.NewUlid())
            .Should().StartWith(expectedPrefix);
    }

    [Fact]
    public void ATitleWithNoUsableCharacters_StillProducesAValidRef()
    {
        SlugGenerator.GenerateMilestoneBranchName("!!!", Ulid.NewUlid())
            .Should().StartWith("orkka/milestone/milestone-");
    }

    [Fact]
    public void MilestonesCreatedInTheSameMillisecond_GetDistinctBranches()
    {
        // A decomposition seeds several milestones in one loop, so their ULIDs share a
        // timestamp — and a ULID's LEADING characters are timestamp-derived. Suffixing with the
        // head would hand every milestone in a plan the same branch.
        var names = Enumerable.Range(0, 200)
            .Select(_ => SlugGenerator.GenerateMilestoneBranchName("Same title", Ulid.NewUlid()))
            .ToList();

        names.Distinct().Should().HaveCount(names.Count);
    }

    [Fact]
    public void ALongTitle_IsTruncated_ButKeepsItsUniqueSuffix()
    {
        var id = Ulid.NewUlid();
        var name = SlugGenerator.GenerateMilestoneBranchName(new string('a', 300), id);

        name.Should().EndWith(id.ToString()[^8..].ToLowerInvariant());
        name.Length.Should().BeLessThan(80);
    }

    [Fact]
    public void TheNameIsAValidGitRef()
    {
        var name = SlugGenerator.GenerateMilestoneBranchName("Ação São Paulo", Ulid.NewUlid());

        // git refuses refs with spaces, ~ ^ : ? * [ \ and a trailing or doubled slash. The
        // branch is created by the platform, so an invalid one fails the whole milestone.
        name.Should().MatchRegex(@"^orkka/milestone/[a-z0-9\-]+$");
        name.Should().NotContain("--");
        name.Should().NotEndWith("/");
    }
}
