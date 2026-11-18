namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Features.Notifications;
using Orkka.Modules.Organizations.Infrastructure;

[Collection("Integration")]
public sealed class NotificationEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Notification> SeedNotificationAsync(
        string userId = TestCurrentUser.TestUserId,
        bool isRead = false,
        NotificationType type = NotificationType.PlanApprovalRequired)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();

        var notification = new Notification
        {
            OrgId = TestCurrentUser.TestOrgId,
            UserId = userId,
            TaskId = Ulid.NewUlid(),
            Type = type,
            Title = "Seeded task",
            ProjectId = Ulid.NewUlid(),
            ProjectSlug = "seeded-project",
            TaskNumber = 3,
            IsRead = isRead,
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();
        return notification;
    }

    private async Task CleanupAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
        await db.Notifications.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnRowsNewestFirst_WithUnreadCount()
    {
        await CleanupAsync();
        var older = await SeedNotificationAsync();
        var newer = await SeedNotificationAsync(type: NotificationType.TaskCompleted);
        await SeedNotificationAsync(userId: "user_someone_else");
        await SeedNotificationAsync(isRead: true, type: NotificationType.TaskFailed);

        var response = await _client.GetFromJsonAsync<ListNotificationsResponse>(
            "/api/notifications", TestJsonOptions.Default);

        response!.Items.Should().HaveCount(3);
        response.Items.Should().OnlyContain(n => n.ProjectSlug == "seeded-project");
        response.Items.Select(n => n.Id).Should().ContainInOrder(newer.Id, older.Id);
        response.UnreadCount.Should().Be(2);
        response.Items.First(n => n.Id == newer.Id).Type.Should().Be("TaskCompleted");
    }

    [Fact]
    public async Task MarkRead_OwnRow_SetsIsRead()
    {
        await CleanupAsync();
        var row = await SeedNotificationAsync();

        var response = await _client.PostAsync($"/api/notifications/{row.Id}/read", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var list = await _client.GetFromJsonAsync<ListNotificationsResponse>(
            "/api/notifications", TestJsonOptions.Default);
        list!.UnreadCount.Should().Be(0);
        list.Items.Single().IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task MarkRead_AnotherUsersRow_Returns404AndLeavesItUnread()
    {
        await CleanupAsync();
        var foreign = await SeedNotificationAsync(userId: "user_someone_else");

        var response = await _client.PostAsync($"/api/notifications/{foreign.Id}/read", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
        (await db.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == foreign.Id))
            .IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task ReadAll_MarksOnlyOwnRows()
    {
        await CleanupAsync();
        await SeedNotificationAsync();
        await SeedNotificationAsync(type: NotificationType.QuestionAsked);
        var foreign = await SeedNotificationAsync(userId: "user_someone_else");

        var response = await _client.PostAsync("/api/notifications/read-all", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var list = await _client.GetFromJsonAsync<ListNotificationsResponse>(
            "/api/notifications", TestJsonOptions.Default);
        list!.UnreadCount.Should().Be(0);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
        (await db.Notifications.IgnoreQueryFilters().SingleAsync(n => n.Id == foreign.Id))
            .IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task List_RespectsLimit()
    {
        await CleanupAsync();
        for (var i = 0; i < 5; i++)
            await SeedNotificationAsync(isRead: i % 2 == 0, type: NotificationType.TaskCompleted);

        var response = await _client.GetFromJsonAsync<ListNotificationsResponse>(
            "/api/notifications?limit=2", TestJsonOptions.Default);

        response!.Items.Should().HaveCount(2);
        // UnreadCount covers everything, not just the returned page (i = 0,2,4 seeded read).
        response.UnreadCount.Should().Be(2);
    }
}
