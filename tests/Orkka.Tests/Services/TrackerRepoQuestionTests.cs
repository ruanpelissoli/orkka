namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Commands;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Infrastructure.Services;

/// <summary>
/// Reading the answer to a tracker task's "which repository?" question, and the refused
/// commands a runner reports for the Machine page.
/// </summary>
public sealed class TrackerRepoQuestionTests
{
    private static readonly Ulid Api = Ulid.NewUlid();
    private static readonly Ulid ShopApi = Ulid.NewUlid();
    private static readonly Ulid Web = Ulid.NewUlid();

    private static readonly RepoSummary[] Repos =
    [
        new(Api, Ulid.NewUlid(), Ulid.NewUlid(), "api", "x", "main", "#fff", WorkspaceStatus.Ready),
        new(ShopApi, Ulid.NewUlid(), Ulid.NewUlid(), "shop-api", "x", "main", "#fff", WorkspaceStatus.Ready),
        new(Web, Ulid.NewUlid(), Ulid.NewUlid(), "web", "x", "main", "#fff", WorkspaceStatus.Ready),
    ];

    [Theory]
    [InlineData("shop-api", new[] { 1 })]
    [InlineData("**Which repository should this ticket be worked in?**\nshop-api, web", new[] { 1, 2 })]
    [InlineData("API", new[] { 0 })]
    [InlineData("the frontend please", new int[0])]
    public void Match_FindsTheNamedRepositories_WithoutMatchingInsideLongerNames(string answer, int[] expected)
        => TrackerRepoQuestion.Match(answer, Repos).Select(r => Array.IndexOf(Repos, r)).Should().Equal(expected);

    [Fact]
    public void Match_AcceptsARepositoryId()
        => TrackerRepoQuestion.Match($"use {Web}", Repos).Should().ContainSingle().Which.Id.Should().Be(Web);

    [Fact]
    public void Metadata_RecognisesOnlyTheRoutingQuestion()
    {
        TrackerRepoQuestion.ParseMetadata("""{"trackerRepoQuestion":true,"submitOnAnswer":true}""")!.SubmitOnAnswer.Should().BeTrue();
        TrackerRepoQuestion.ParseMetadata("""{"autoAnswered":true}""").Should().BeNull();
        TrackerRepoQuestion.ParseMetadata("not json").Should().BeNull();
    }

    [Fact]
    public void PendingApprovals_AreThisWorkspacesRefusedCommands_NewestFirst_AtMostTwenty()
    {
        var root = Path.Combine(Path.GetTempPath(), "orkka-approvals-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CommandApprovalStore(root);
            for (var i = 0; i < 25; i++)
                store.RecordPending("github.com/acme/shop", $"npm run step-{i}", CommandKind.Setup, "acme");
            store.RecordPending("github.com/other/x", "make", CommandKind.Check, "other-job");
            var approved = store.RecordPending("github.com/acme/shop", "npm ci", CommandKind.Setup, "acme");
            store.Approve(approved.Id);

            var pending = RunnerSessionFactory.PendingApprovals(root, "acme");

            pending.Should().HaveCount(20);
            pending.Should().OnlyContain(p => p.Repo == "github.com/acme/shop" && p.Command != "npm ci");
            pending[0].Machine.Should().Be(Environment.MachineName);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
