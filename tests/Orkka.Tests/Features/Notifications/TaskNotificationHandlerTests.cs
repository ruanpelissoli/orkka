namespace Orkka.Tests.Features.Notifications;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Features.Notifications;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Events.Catalog;

public sealed class TaskNotificationHandlerTests : IDisposable
{
    private readonly OrganizationsDbContext _db;
    private readonly Mock<ITaskNotifier> _notifierMock = new();
    private readonly Mock<IProjectsApi> _projectsApiMock = new();

    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _projectId = Ulid.NewUlid();
    private readonly Ulid _taskId = Ulid.NewUlid();

    public TaskNotificationHandlerTests()
    {
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new OrganizationsDbContext(options);

        _projectsApiMock
            .Setup(p => p.GetProjectSummaryAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectSummary(
                _projectId, _orgId, "Acme App", "acme-app", "ACM",
                null));
    }

    public void Dispose() => _db.Dispose();

    private NotificationPublisher Publisher => new(
        _db, _notifierMock.Object, NullLogger<NotificationPublisher>.Instance);

    private TaskStatusChangedNotificationHandler StatusHandler => new(
        _db, Publisher, _projectsApiMock.Object);

    private TaskMergeFailedNotificationHandler MergeHandler => new(
        _db, Publisher, _projectsApiMock.Object);

    private TaskStatusChanged StatusEvent(
        string status, string? creator = "user_creator", string? actor = null) => new(
        _taskId, _orgId, _projectId, 7, "Add login page", creator, status, actor);

    [Theory]
    [InlineData("WaitingPlanApproval", NotificationType.PlanApprovalRequired)]
    [InlineData("WaitingAnswer", NotificationType.QuestionAsked)]
    [InlineData("WaitingCodeReview", NotificationType.CodeReviewRequired)]
    [InlineData("WaitingQaReview", NotificationType.QaReviewRequired)]
    [InlineData("WaitingUserVerification", NotificationType.VerificationRequired)]
    [InlineData("Blocked", NotificationType.TaskFailed)]
    [InlineData("ReasoningTimeout", NotificationType.TaskFailed)]
    [InlineData("OutOfTokens", NotificationType.TaskFailed)]
    [InlineData("Completed", NotificationType.TaskCompleted)]
    public async Task StatusChanged_AttentionStatus_CreatesNotificationAndPushes(
        string status, NotificationType expectedType)
    {
        await StatusHandler.HandleAsync(StatusEvent(status), CancellationToken.None);

        var row = _db.Notifications.Single();
        row.OrgId.Should().Be(_orgId);
        row.UserId.Should().Be("user_creator");
        row.TaskId.Should().Be(_taskId);
        row.Type.Should().Be(expectedType);
        row.Title.Should().Be("Add login page");
        row.ProjectId.Should().Be(_projectId);
        row.ProjectSlug.Should().Be("acme-app");
        row.TaskNumber.Should().Be(7);
        row.IsRead.Should().BeFalse();

        _notifierMock.Verify(n => n.SendNotificationCreatedAsync(
            "user_creator",
            It.Is<NotificationCreatedEvent>(e =>
                e.Id == row.Id
                && e.Type == expectedType.ToString()
                && e.ProjectSlug == "acme-app"
                && e.TaskNumber == 7)), Times.Once);
    }

    [Fact]
    public async Task StatusChanged_NonAttentionStatus_DoesNothing()
    {
        await StatusHandler.HandleAsync(StatusEvent("Active"), CancellationToken.None);

        _db.Notifications.Should().BeEmpty();
        _notifierMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StatusChanged_NullCreator_DoesNothing()
    {
        await StatusHandler.HandleAsync(StatusEvent("Completed", creator: null), CancellationToken.None);

        _db.Notifications.Should().BeEmpty();
        _notifierMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StatusChanged_ActorIsCreator_DoesNothing()
    {
        await StatusHandler.HandleAsync(
            StatusEvent("Completed", creator: "user_creator", actor: "user_creator"),
            CancellationToken.None);

        _db.Notifications.Should().BeEmpty();
        _notifierMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StatusChanged_OtherActor_StillNotifiesCreator()
    {
        await StatusHandler.HandleAsync(
            StatusEvent("Blocked", creator: "user_creator", actor: "user_teammate"),
            CancellationToken.None);

        _db.Notifications.Single().UserId.Should().Be("user_creator");
    }

    [Fact]
    public async Task StatusChanged_UnreadDuplicate_IsSkipped()
    {
        await StatusHandler.HandleAsync(StatusEvent("WaitingAnswer"), CancellationToken.None);
        await StatusHandler.HandleAsync(StatusEvent("WaitingAnswer"), CancellationToken.None);

        _db.Notifications.Should().HaveCount(1);
    }

    [Fact]
    public async Task StatusChanged_ReadDuplicate_CreatesFreshNotification()
    {
        await StatusHandler.HandleAsync(StatusEvent("WaitingAnswer"), CancellationToken.None);
        var first = _db.Notifications.Single();
        first.IsRead = true;
        await _db.SaveChangesAsync();

        await StatusHandler.HandleAsync(StatusEvent("WaitingAnswer"), CancellationToken.None);

        _db.Notifications.Should().HaveCount(2);
    }

    [Fact]
    public async Task StatusChanged_PushFailure_StillPersistsRow()
    {
        _notifierMock
            .Setup(n => n.SendNotificationCreatedAsync(It.IsAny<string>(), It.IsAny<NotificationCreatedEvent>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var act = () => StatusHandler.HandleAsync(StatusEvent("Completed"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        _db.Notifications.Should().HaveCount(1);
    }

    [Fact]
    public async Task MergeFailed_CreatesNotificationWithReasonBody()
    {
        var evt = new TaskMergeAttemptFailed(
            _taskId, _orgId, _projectId, 7, "Add login page",
            "user_creator", "Merge conflict in app.ts", ActorUserId: null);

        await MergeHandler.HandleAsync(evt, CancellationToken.None);

        var row = _db.Notifications.Single();
        row.Type.Should().Be(NotificationType.TaskMergeFailed);
        row.Body.Should().Be("Merge conflict in app.ts");
    }
}
