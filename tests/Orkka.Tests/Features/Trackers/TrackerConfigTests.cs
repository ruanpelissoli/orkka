namespace Orkka.Tests.Features.Trackers;

using FluentAssertions;
using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Tasks.Contracts;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Trackers.Features.ProjectConfig;
using Orkka.Modules.Trackers.Infrastructure;
using Orkka.Modules.Trackers.PublicApi;

/// <summary>
/// A project's ticket source: the subfolder rule (it becomes a working directory on the
/// developer's machine), the save validator, and the connections a runner reports in its Hello.
/// </summary>
public sealed class TrackerConfigTests : IDisposable
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private const string UserId = "user_dana";

    private readonly TrackersDbContext _db = new(
        new DbContextOptionsBuilder<TrackersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options,
        currentUser: null);

    public void Dispose() => _db.Dispose();

    // ── Subfolders ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("packages/api", "packages/api")]
    [InlineData("packages/api/", "packages/api")]
    [InlineData(@"packages\api", "packages/api")]
    [InlineData("apps//web", "apps/web")]
    public void Subpath_IsNormalizedToForwardSlashes(string? input, string? expected)
    {
        TrackerSubpath.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("packages/../../etc")]
    [InlineData("./api")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows")]
    [InlineData("~/code")]
    public void Subpath_ThatCouldLeaveTheRepository_IsRefused(string input)
        => TrackerSubpath.TryNormalize(input, out _).Should().BeFalse();

    // ── Save validator ──────────────────────────────────────────────────────

    private static SaveProjectTrackerRequest Valid(params TrackerEpicMappingDto[] epics) => new(
        Ulid.NewUlid(), "SHOP",
        epics.Length > 0 ? epics : [new TrackerEpicMappingDto("SHOP-1", "Checkout", Ulid.NewUlid(), null)]);

    [Fact]
    public void Validator_AcceptsAMinimalSource()
        => new SaveProjectTrackerValidator().TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Validator_NeedsNoEpic_WhenAllMyTicketsArePickedUp()
    {
        var validator = new SaveProjectTrackerValidator();
        validator.TestValidate(Valid() with { Epics = [] }).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(Valid() with { Epics = null, ScopeMode = "All" }).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validator_AnEpicWithoutARepository_IsFine()
        => new SaveProjectTrackerValidator()
            .TestValidate(Valid(new TrackerEpicMappingDto("SHOP-1", null, null, null)) with { ScopeMode = "Epics" })
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Validator_RoutingRules_MatchAComponentLabelOrAreaPath()
    {
        var validator = new SaveProjectTrackerValidator();
        validator.TestValidate(Valid() with
        {
            RoutingRules =
            [
                new TrackerRoutingRuleDto("component", "Payments", Ulid.NewUlid(), null),
                new TrackerRoutingRuleDto("label", "frontend", Ulid.NewUlid(), "apps/web"),
                new TrackerRoutingRuleDto("areaPath", @"Shop\Backend", Ulid.NewUlid(), null),
            ],
        }).ShouldNotHaveAnyValidationErrors();

        validator.TestValidate(Valid() with { RoutingRules = [new TrackerRoutingRuleDto("epic", "SHOP-1", Ulid.NewUlid(), null)] })
            .Errors.Should().ContainSingle(e => e.PropertyName.EndsWith("Kind"));
        validator.TestValidate(Valid() with { RoutingRules = [new TrackerRoutingRuleDto("color", "red", Ulid.NewUlid(), null)] })
            .Errors.Should().ContainSingle(e => e.PropertyName.EndsWith("Kind"));
        validator.TestValidate(Valid() with { ScopeMode = "Sometimes" }).ShouldHaveValidationErrorFor(r => r.ScopeMode);
    }

    [Fact]
    public void IntakeStatuses_DefaultToToDo()
    {
        ProjectTrackerMappers.ParseIntakeStatuses(null).Should().Equal("To Do");
        ProjectTrackerMappers.ParseIntakeStatuses("[]").Should().Equal("To Do");
        ProjectTrackerMappers.SerializeIntakeStatuses([" Selected for Development ", "To Do", "to do"])
            .Should().Be("""["Selected for Development","To Do"]""");
    }

    [Fact]
    public void Validator_AnEmptyScope_SaysWhatToPick()
        => new SaveProjectTrackerValidator().TestValidate(Valid() with { Scope = "" })
            .ShouldHaveValidationErrorFor(r => r.Scope)
            .WithErrorMessage("Pick the Jira project, Azure DevOps project or GitHub owner the tickets live in.");

    [Fact]
    public void Validator_InEpicsMode_RequiresAtLeastOneEpic_EachMappedOnce()
    {
        var validator = new SaveProjectTrackerValidator();
        validator.TestValidate(Valid() with { Epics = [], ScopeMode = "Epics" }).ShouldHaveValidationErrorFor(r => r.Epics);

        var repo = Ulid.NewUlid();
        validator.TestValidate(Valid(
                new TrackerEpicMappingDto("SHOP-1", null, repo, null),
                new TrackerEpicMappingDto("shop-1", null, repo, "web")))
            .ShouldHaveValidationErrorFor(r => r.Epics);
    }

    [Theory]
    [InlineData(59)]
    [InlineData(301)]
    public void Validator_KeepsThePollIntervalBetweenOneAndFiveMinutes(int seconds)
        => new SaveProjectTrackerValidator().TestValidate(Valid() with { PollIntervalSeconds = seconds })
            .ShouldHaveValidationErrorFor(r => r.PollIntervalSeconds);

    [Fact]
    public void Validator_RequiresTheTicketKeyInTheBranchTemplate()
        => new SaveProjectTrackerValidator().TestValidate(Valid() with { BranchTemplate = "feature/{slug}" })
            .ShouldHaveValidationErrorFor(r => r.BranchTemplate);

    [Fact]
    public void Validator_RefusesAnEscapingSubfolder()
        => new SaveProjectTrackerValidator()
            .TestValidate(Valid(new TrackerEpicMappingDto("SHOP-1", null, Ulid.NewUlid(), "../other-repo")))
            .Errors.Should().ContainSingle(e => e.PropertyName.EndsWith("Subpath"));

    // ── Default status map ──────────────────────────────────────────────────

    [Fact]
    public void Defaults_Jira_StartedIsInProgress_PrOpenedIsTheReviewStatus_IntakeIsToDo()
    {
        var defaults = Orkka.Modules.Trackers.Domain.TrackerStatusDefaults.Compute(TrackerKinds.Jira,
        [
            new TrackerStatus("Backlog", "todo"),
            new TrackerStatus("To Do", "todo"),
            new TrackerStatus("In Progress", "inprogress"),
            new TrackerStatus("Code Review", "inprogress"),
            new TrackerStatus("Done", "done"),
        ]);

        defaults.StatusMap.Planning.Should().Be("In Progress");
        defaults.StatusMap.PrOpened.Should().Be("Code Review");
        defaults.StatusMap.Merged.Should().BeNull("teams close tickets their own way");
        defaults.IntakeStatuses.Should().Equal("To Do");
    }

    [Fact]
    public void Defaults_Jira_FallBackToTheCategories()
    {
        var defaults = Orkka.Modules.Trackers.Domain.TrackerStatusDefaults.Compute(TrackerKinds.Jira,
        [
            new TrackerStatus("Open", "todo"),
            new TrackerStatus("Building", "inprogress"),
            new TrackerStatus("Shipped", "done"),
        ]);

        defaults.StatusMap.Planning.Should().Be("Building");
        defaults.StatusMap.PrOpened.Should().BeNull();
        defaults.IntakeStatuses.Should().Equal("Open");
    }

    [Fact]
    public void Defaults_AzureDevOps_ProposedIsIntake_ResolvedIsPrOpened()
    {
        var defaults = Orkka.Modules.Trackers.Domain.TrackerStatusDefaults.Compute(TrackerKinds.AzureDevOps,
        [
            new TrackerStatus("New", "todo", "Proposed"),
            new TrackerStatus("Approved", "todo", "Proposed"),
            new TrackerStatus("Committed", "inprogress", "InProgress"),
            new TrackerStatus("Resolved", "inprogress", "Resolved"),
            new TrackerStatus("Closed", "done", "Completed"),
        ]);

        defaults.StatusMap.Planning.Should().Be("Committed");
        defaults.StatusMap.PrOpened.Should().Be("Resolved");
        defaults.IntakeStatuses.Should().Equal("New", "Approved");
    }

    // ── Connections from the Hello ──────────────────────────────────────────

    private static RunnerTrackerConnection Jira(string id = "jira-acme", bool healthy = true) => new(
        id, TrackerKinds.Jira, "https://acme.atlassian.net", "acc-1", "Dana", healthy,
        healthy ? null : "401 Unauthorized", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Hello_RecordsEachConnection_AndUpdatesItOnTheNext()
    {
        var api = new TrackersApi(_db, Mock.Of<ITasksApi>(), Mock.Of<Orkka.Modules.Projects.Contracts.IProjectsApi>(), Mock.Of<IRepoRouter>(), NullLogger<TrackersApi>.Instance);

        await api.UpsertRunnerConnectionsAsync(OrgId, UserId, "dana-laptop", [Jira()]);
        await api.UpsertRunnerConnectionsAsync(OrgId, UserId, "dana-laptop", [Jira(healthy: false)]);

        var row = await _db.Connections.IgnoreQueryFilters().SingleAsync();
        row.ConnectionKey.Should().Be("jira-acme");
        row.SiteUrl.Should().Be("https://acme.atlassian.net");
        row.Healthy.Should().BeFalse();
        row.HealthDetail.Should().Be("401 Unauthorized");
        row.MachineName.Should().Be("dana-laptop");
    }

    [Fact]
    public async Task AConnectionRemovedOnOneMachine_IsMarkedMissing_WithoutTouchingTheOthers()
    {
        var api = new TrackersApi(_db, Mock.Of<ITasksApi>(), Mock.Of<Orkka.Modules.Projects.Contracts.IProjectsApi>(), Mock.Of<IRepoRouter>(), NullLogger<TrackersApi>.Instance);
        await api.UpsertRunnerConnectionsAsync(OrgId, UserId, "dana-laptop", [Jira("jira-acme")]);
        await api.UpsertRunnerConnectionsAsync(OrgId, UserId, "dana-desktop",
            [Jira("ado-contoso") with { Kind = TrackerKinds.AzureDevOps, SiteUrl = "https://dev.azure.com/contoso" }]);

        // The laptop no longer holds its connection; the desktop's is not the laptop's to drop.
        await api.UpsertRunnerConnectionsAsync(OrgId, UserId, "dana-laptop", []);

        var rows = await _db.Connections.IgnoreQueryFilters().ToListAsync();
        rows.Should().HaveCount(2, "a project may still point at it — its configuration is kept");
        rows.Single(r => r.ConnectionKey == "jira-acme").Healthy.Should().BeFalse();
        rows.Single(r => r.ConnectionKey == "jira-acme").HealthDetail.Should().Contain("./orkka tracker add");
        rows.Single(r => r.ConnectionKey == "ado-contoso").Healthy.Should().BeTrue();
    }

    [Fact]
    public async Task Hello_NeverMixesUsers()
    {
        var api = new TrackersApi(_db, Mock.Of<ITasksApi>(), Mock.Of<Orkka.Modules.Projects.Contracts.IProjectsApi>(), Mock.Of<IRepoRouter>(), NullLogger<TrackersApi>.Instance);
        await api.UpsertRunnerConnectionsAsync(OrgId, UserId, "dana-laptop", [Jira()]);
        await api.UpsertRunnerConnectionsAsync(OrgId, "user_sam", "sam-laptop", [Jira()]);

        (await _db.Connections.IgnoreQueryFilters().CountAsync()).Should().Be(2,
            "the same key on two people's machines is two connections with two tokens");
    }
}
