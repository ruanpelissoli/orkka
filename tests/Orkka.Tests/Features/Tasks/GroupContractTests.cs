namespace Orkka.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using Orkka.Modules.Tasks.PublicApi;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// What a group member's agent is handed, and what happens to the contract once one of them has
/// actually shipped.
///
/// Two properties matter more than the text itself. A task that is NOT in a group must read
/// exactly as it did before any of this existed — the contract section is skipped entirely, so
/// the overwhelming majority of tasks are untouched. And the agreed contract must survive the
/// "as built" record: the other halves were given the agreed text and may already have built
/// against it, so a substitution would move the target under them without anyone noticing.
/// </summary>
public sealed class GroupContractTests : IDisposable
{
    private readonly TasksDbContext _db;
    private readonly TasksApi _api;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private int _number;

    public GroupContractTests()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TasksDbContext(options, currentUser: null, eventBus: null);

        _api = new TasksApi(
            _db,
            Mock.Of<ITaskNumberService>(),
            Mock.Of<IProjectsApi>(),
            Mock.Of<ITaskNotifier>(),
            Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
            new TaskSubmissionService(
                _db,
                Mock.Of<ITaskNotifier>(),
                new MilestoneProgressUpdater(
                    _db,
                    Mock.Of<ITaskNotifier>(),
                    Mock.Of<Orkka.SharedKernel.Events.IEventBus>(),
                    NullLogger<MilestoneProgressUpdater>.Instance),
                Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                NullLogger<TaskSubmissionService>.Instance),
            NullLogger<TasksApi>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private const string Agreed = "### Endpoints\nPOST /api/users/{id}/avatar -> 201 {\"url\": string}";

    private async Task<TaskGroup> SeedGroupAsync(string? contract = Agreed)
    {
        var group = new TaskGroup
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            Title = "Add avatar upload",
            Contract = contract,
            Status = TaskGroupStatus.Active,
        };
        _db.TaskGroups.Add(group);
        await _db.SaveChangesAsync();
        return group;
    }

    private async Task<TaskEntity> SeedTaskAsync(Ulid? groupId, string title)
    {
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = _orgId,
            ProjectId = _projectId,
            GroupId = groupId,
            RepoId = Ulid.NewUlid(),
            Number = ++_number,
            Title = title,
            Status = TaskStatus.Active,
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    [Fact]
    public async Task UngroupedTask_HasNoContract_SoItsPromptIsUnchanged()
    {
        var task = await SeedTaskAsync(groupId: null, "Ordinary task");

        // Null is what makes the orchestrator skip the whole section. Every task that predates
        // this feature takes this path.
        (await _api.GetGroupContractAsync(task.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Member_GetsTheContractAndItsSiblings_ButNotItself()
    {
        var group = await SeedGroupAsync();
        var provider = await SeedTaskAsync(group.Id, "Avatar upload endpoint");
        await SeedTaskAsync(group.Id, "Avatar upload screen");

        var contract = await _api.GetGroupContractAsync(provider.Id);

        contract.Should().NotBeNull();
        contract!.Contract.Should().Be(Agreed);
        contract.Title.Should().Be("Add avatar upload");
        // Listing itself would tell the agent not to build the thing it was assigned.
        contract.MemberSummaries.Should().ContainSingle()
            .Which.Should().Contain("Avatar upload screen");
    }

    [Fact]
    public async Task Member_OfAGroupWithNoContract_StillReportsMembership()
    {
        // A split that produced tasks but no contract — the model found nothing crossing the
        // boundary. The orchestrator skips the section on empty text, but the group is real.
        var group = await SeedGroupAsync(contract: null);
        var task = await SeedTaskAsync(group.Id, "One half");

        var contract = await _api.GetGroupContractAsync(task.Id);

        contract.Should().NotBeNull();
        contract!.Contract.Should().BeNull();
    }

    [Fact]
    public async Task AsBuilt_IsAppended_NeverReplacingTheAgreedContract()
    {
        var group = await SeedGroupAsync();

        await _api.AppendGroupAsBuiltAsync(group.Id, 12, "Avatar upload endpoint", "### Endpoints\nPOST /api/users/{id}/photo");

        var reloaded = await _db.TaskGroups.AsNoTracking().FirstAsync(g => g.Id == group.Id);

        // Both survive. The disagreement between them is the signal — the agreed shape is what
        // the sibling built against, and silently adopting the new one would hide the break.
        reloaded.Contract.Should().Contain("POST /api/users/{id}/avatar");
        reloaded.Contract.Should().Contain("POST /api/users/{id}/photo");
        reloaded.Contract.Should().Contain("As built — #12 Avatar upload endpoint");
    }

    [Fact]
    public async Task AsBuilt_IsIdempotent_AcrossReviewBounces()
    {
        var group = await SeedGroupAsync();

        // Developing is re-entered on a review bounce or a QA failure, and each pass publishes
        // the surface again. Appending every time would push the agreed text out of the budget.
        await _api.AppendGroupAsBuiltAsync(group.Id, 12, "Endpoint", "### Endpoints\nPOST /a");
        await _api.AppendGroupAsBuiltAsync(group.Id, 12, "Endpoint", "### Endpoints\nPOST /a");
        await _api.AppendGroupAsBuiltAsync(group.Id, 12, "Endpoint", "### Endpoints\nPOST /a");

        var reloaded = await _db.TaskGroups.AsNoTracking().FirstAsync(g => g.Id == group.Id);

        var occurrences = reloaded.Contract!.Split("As built — #12").Length - 1;
        occurrences.Should().Be(1);
    }

    [Fact]
    public async Task AsBuilt_KeepsEachMemberSeparate()
    {
        var group = await SeedGroupAsync();

        await _api.AppendGroupAsBuiltAsync(group.Id, 12, "Endpoint", "### Endpoints\nPOST /a");
        await _api.AppendGroupAsBuiltAsync(group.Id, 13, "Screen", "### Endpoints\nGET /b");

        var reloaded = await _db.TaskGroups.AsNoTracking().FirstAsync(g => g.Id == group.Id);

        reloaded.Contract.Should().Contain("As built — #12 Endpoint");
        reloaded.Contract.Should().Contain("As built — #13 Screen");
    }

    [Fact]
    public async Task AsBuilt_OnAGroupThatVanished_IsANoOp()
    {
        // The group was deleted between the run finishing and its surface being published.
        // Publishing is best-effort and must never fail a task that already shipped its code.
        var act = async () => await _api.AppendGroupAsBuiltAsync(
            Ulid.NewUlid(), 1, "Gone", "### Endpoints\nPOST /a");

        await act.Should().NotThrowAsync();
    }
}
