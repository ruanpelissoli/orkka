namespace Orkka.Tests.Hubs;

using Orkka.Modules.Tasks.Features.Hubs;
using Orkka.Modules.Tasks.Contracts;
using Microsoft.AspNetCore.SignalR;
using Moq;

public sealed class TaskNotifierTests
{
    private readonly Mock<IHubContext<TaskHub>> _hubContextMock = new();
    private readonly Mock<IHubClients> _clientsMock = new();
    private readonly Mock<IClientProxy> _clientProxyMock = new();
    private readonly TaskNotifier _notifier;

    public TaskNotifierTests()
    {
        _hubContextMock.Setup(h => h.Clients).Returns(_clientsMock.Object);
        _clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxyMock.Object);
        _notifier = new TaskNotifier(_hubContextMock.Object);
    }

    [Fact]
    public async Task SendStreamEvent_SendsToTaskGroup()
    {
        var taskId = Ulid.NewUlid();
        var evt = new StreamEvent("AgentThinking", "Analyzing...", DateTimeOffset.UtcNow);

        await _notifier.SendStreamEventAsync(taskId, evt);

        _clientsMock.Verify(c => c.Group($"task:{taskId}"), Times.Once);
        _clientProxyMock.Verify(c => c.SendCoreAsync("StreamEvent",
            It.Is<object[]>(args => args.Length == 1 && args[0].Equals(evt)),
            default), Times.Once);
    }

    [Fact]
    public async Task SendNotificationCreated_SendsToUserGroup()
    {
        var evt = new NotificationCreatedEvent(
            Ulid.NewUlid(), "user_123", Ulid.NewUlid(), "PlanApprovalRequired",
            "Add login page", null, Ulid.NewUlid(), "acme-app", 7, DateTimeOffset.UtcNow);

        await _notifier.SendNotificationCreatedAsync("user_123", evt);

        _clientsMock.Verify(c => c.Group("user:user_123"), Times.Once);
        _clientProxyMock.Verify(c => c.SendCoreAsync("NotificationCreated",
            It.Is<object[]>(args => args.Length == 1 && args[0].Equals(evt)),
            default), Times.Once);
    }

    [Fact]
    public async Task SendLaneChanged_SendsToBoardAndTaskGroups()
    {
        var projectId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();
        var evt = new LaneChangedEvent(taskId, "My Task", "Ready", "Planning", "Active", DateTimeOffset.UtcNow);

        await _notifier.SendLaneChangedAsync(projectId, evt);

        _clientsMock.Verify(c => c.Group($"board:{projectId}"), Times.Once);
        _clientsMock.Verify(c => c.Group($"task:{taskId}"), Times.Once);
    }

    [Fact]
    public async Task SendStatusChanged_SendsToBoardAndTaskGroups()
    {
        var projectId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();
        var evt = new StatusChangedEvent(taskId, "Blocked", DateTimeOffset.UtcNow);

        await _notifier.SendStatusChangedAsync(projectId, evt);

        _clientsMock.Verify(c => c.Group($"board:{projectId}"), Times.Once);
        _clientsMock.Verify(c => c.Group($"task:{taskId}"), Times.Once);
        _clientProxyMock.Verify(c => c.SendCoreAsync("StatusChanged",
            It.Is<object[]>(args => args.Length == 1 && args[0].Equals(evt)),
            default), Times.Exactly(2));
    }

    [Fact]
    public async Task SendTaskDeleted_SendsToBoardAndTaskGroups()
    {
        var projectId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();
        var evt = new TaskDeletedEvent(taskId, DateTimeOffset.UtcNow);

        await _notifier.SendTaskDeletedAsync(projectId, evt);

        _clientsMock.Verify(c => c.Group($"board:{projectId}"), Times.Once);
        _clientsMock.Verify(c => c.Group($"task:{taskId}"), Times.Once);
        _clientProxyMock.Verify(c => c.SendCoreAsync("TaskDeleted",
            It.Is<object[]>(args => args.Length == 1 && args[0].Equals(evt)),
            default), Times.Exactly(2));
    }

    [Fact]
    public async Task SendNewMessage_SendsToTaskGroup()
    {
        var taskId = Ulid.NewUlid();
        var evt = new NewMessageEvent(taskId, Ulid.NewUlid(), "user", "Hello", "Text", "user_123", DateTimeOffset.UtcNow);

        await _notifier.SendNewMessageAsync(taskId, evt);

        _clientsMock.Verify(c => c.Group($"task:{taskId}"), Times.Once);
        _clientProxyMock.Verify(c => c.SendCoreAsync("NewMessage",
            It.Is<object[]>(args => args.Length == 1 && args[0].Equals(evt)),
            default), Times.Once);
    }

    [Fact]
    public async Task SendTaskUpdated_SendsToBoardAndTaskGroups()
    {
        var projectId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();
        var evt = new TaskUpdatedEvent(taskId, "New Title", null, "Active", "Planning", false, DateTimeOffset.UtcNow);

        await _notifier.SendTaskUpdatedAsync(projectId, evt);

        _clientsMock.Verify(c => c.Group($"board:{projectId}"), Times.Once);
        _clientsMock.Verify(c => c.Group($"task:{taskId}"), Times.Once);
    }

    [Fact]
    public async Task SendPlanReviewed_SendsToTaskGroup()
    {
        var taskId = Ulid.NewUlid();
        var planId = Ulid.NewUlid();
        var evt = new PlanReviewedEvent(taskId, planId, "approve", "Looks good!", DateTimeOffset.UtcNow);

        await _notifier.SendPlanReviewedAsync(taskId, evt);

        _clientsMock.Verify(c => c.Group($"task:{taskId}"), Times.Once);
        _clientProxyMock.Verify(c => c.SendCoreAsync("PlanReviewed",
            It.Is<object[]>(args => args.Length == 1 && args[0].Equals(evt)),
            default), Times.Once);
    }
}
