namespace Orkka.Tests.Features.Tasks;

using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Contracts;
using FluentAssertions;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed class TaskMappersTests
{
    [Fact]
    public void ToResponse_MapsAllFields()
    {
        var projectId = Ulid.NewUlid();
        var repo = new Orkka.Modules.Projects.Contracts.RepoSummary(
            Ulid.NewUlid(), projectId, Ulid.NewUlid(), "my-repo",
            "https://github.com/test/repo.git", "main", "#FF5733",
            WorkspaceStatus.Ready);

        var entity = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            ProjectId = projectId,
            RepoId = repo.Id,
            Number = 42,
            Title = "Fix bug",
            Description = "Some bug",
            Priority = Priority.High,
            CurrentLane = Lane.Planning,
            Status = TaskStatus.Active,
            BranchName = "feat/fix-bug",
            TotalTokensUsed = 5000,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var response = entity.ToResponse("ORK", repo);

        response.Id.Should().Be(entity.Id);
        response.Number.Should().Be(42);
        response.Code.Should().Be("ORK-42");
        response.Title.Should().Be("Fix bug");
        response.Priority.Should().Be("High");
        response.CurrentLane.Should().Be("Planning");
        response.Status.Should().Be("Active");
        response.RepoName.Should().Be("my-repo");
        response.RepoCardColor.Should().Be("#FF5733");
        response.TotalTokensUsed.Should().Be(5000);
        response.BranchName.Should().Be("feat/fix-bug");
    }

    [Fact]
    public void ToResponse_NullRepo_ReturnsNullRepoFields()
    {
        var entity = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Title = "Task",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var response = entity.ToResponse("ORK");

        response.RepoName.Should().BeNull();
        response.RepoCardColor.Should().BeNull();
    }

    [Fact]
    public void ToSummary_MapsCorrectly()
    {
        var entity = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Title = "Summary task",
            Priority = Priority.Critical,
            CurrentLane = Lane.Ready,
            Status = TaskStatus.InQueue,
            QueuePosition = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var summary = entity.ToSummary("ORK");

        summary.Title.Should().Be("Summary task");
        summary.Priority.Should().Be("Critical");
        summary.CurrentLane.Should().Be("Ready");
        summary.Status.Should().Be("InQueue");
        summary.QueuePosition.Should().Be(1);
    }

    [Fact]
    public void SerializeTags_NullReturnsNull()
    {
        Mappers.SerializeTags(null).Should().BeNull();
    }

    [Fact]
    public void SerializeTags_ListReturnsJson()
    {
        var tags = new List<string> { "bug", "frontend" };
        var json = Mappers.SerializeTags(tags);
        json.Should().Contain("bug");
        json.Should().Contain("frontend");
    }

    // ── RunnerUserId ────────────────────────────────────────────────────────

    [Fact]
    public void RunnerUserId_IsMapped_OnBothResponses()
    {
        var entity = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Title = "Routed",
            RunnerUserId = "user_owner",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        entity.ToResponse("ORK").RunnerUserId.Should().Be("user_owner");
        entity.ToSummary("ORK").RunnerUserId.Should().Be("user_owner");
    }
}
