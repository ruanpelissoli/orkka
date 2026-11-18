namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Orkka.Modules.Tasks.Domain;

/// <summary>
/// An agent's ask to widen its cross-repo task.
///
/// The whole point of routing this through a request instead of an action is that a human
/// decides — so the two things that matter are that a re-entered phase cannot nag the user with
/// the same ask twice, and that a decline is remembered rather than silently reopening.
/// </summary>
public sealed class RepoRequestTests
{
    private static RepoRequest Request(string repo, string status = RepoRequests.Pending) =>
        new(repo, "Needs the endpoint", Ulid.NewUlid().ToString(),
            new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero), status);

    [Fact]
    public void Add_RecordsTheFirstRequest()
    {
        var json = RepoRequests.TryAdd(null, Request("mobile"));

        json.Should().NotBeNull();
        RepoRequests.Parse(json).Should().ContainSingle()
            .Which.RepoName.Should().Be("mobile");
    }

    [Fact]
    public void Add_IsRefused_WhenTheSameRepoIsAlreadyPending()
    {
        var first = RepoRequests.TryAdd(null, Request("mobile"));

        // Developing is re-entered on a review bounce, and the agent hits the same wall again.
        // A second identical card is noise, and declining twice is worse than noise.
        RepoRequests.TryAdd(first, Request("mobile")).Should().BeNull();
    }

    [Fact]
    public void Add_IsRefused_WhenTheSameRepoWasAlreadyApproved()
    {
        var json = RepoRequests.TryAdd(null, Request("mobile"));
        json = RepoRequests.Resolve(json, "mobile", RepoRequests.Approved);

        RepoRequests.TryAdd(json, Request("mobile")).Should().BeNull();
    }

    [Fact]
    public void Add_IsAllowedAgain_AfterADecline()
    {
        var json = RepoRequests.TryAdd(null, Request("mobile"));
        json = RepoRequests.Resolve(json, "mobile", RepoRequests.Declined);

        // Deliberate: a later run may have a better reason, and barring it forever would leave
        // the agent with no way to escalate at all.
        var updated = RepoRequests.TryAdd(json, Request("mobile"));

        updated.Should().NotBeNull();
        RepoRequests.Parse(updated).Should().HaveCount(2);
    }

    [Fact]
    public void Add_MatchesRepoNamesCaseInsensitively()
    {
        var json = RepoRequests.TryAdd(null, Request("Mobile"));

        RepoRequests.TryAdd(json, Request("mobile")).Should().BeNull();
    }

    [Fact]
    public void Resolve_OnlyTouchesPendingEntriesForThatRepo()
    {
        var json = RepoRequests.TryAdd(null, Request("mobile"));
        json = RepoRequests.TryAdd(json, Request("infra"));

        json = RepoRequests.Resolve(json, "mobile", RepoRequests.Approved);

        var requests = RepoRequests.Parse(json);
        requests.Single(r => r.RepoName == "mobile").Status.Should().Be(RepoRequests.Approved);
        requests.Single(r => r.RepoName == "infra").Status.Should().Be(RepoRequests.Pending);
    }

    [Fact]
    public void Parse_OfGarbage_IsEmptyRatherThanAThrow()
    {
        // The request is a suggestion. Losing one to a corrupt row is far cheaper than failing
        // the run that raised it.
        RepoRequests.Parse("{not json").Should().BeEmpty();
        RepoRequests.Parse("").Should().BeEmpty();
        RepoRequests.Parse(null).Should().BeEmpty();
    }
}
