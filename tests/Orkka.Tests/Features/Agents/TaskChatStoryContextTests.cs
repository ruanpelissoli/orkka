namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Features.TaskChat;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// A Discuss turn on a Story member carries the same Story context section as the member's
/// pipeline runs — the Execution module's builder, reached through ITaskStoryContextSource.
/// </summary>
public sealed class TaskChatStoryContextTests
{
    [Fact]
    public async Task AStoryMembersChatPrompt_ContainsTheStorySection()
    {
        var groupId = Ulid.NewUlid();
        var task = TaskChatTests.Task() with { GroupId = groupId, StoryOrder = 2, ExternalKey = "SHOP-12" };
        var first = Ulid.NewUlid();
        var story = new StoryContext(groupId, task.OrgId, task.ProjectId, "[SHOP-10] Checkout", "The whole story.",
            "SHOP-10", null, "feature/SHOP-10-checkout", null, false, 0,
            [
                new StoryMemberContext(first, 11, "[SHOP-11] Cart", 1, null, TaskStatus.Completed, true, false, "aaaaaaaaaa", "Add the cart", []),
                new StoryMemberContext(task.Id, 42, task.Title, 2, null, TaskStatus.Active, false, false, null, null, []),
            ],
            []);

        var tasks = new Mock<ITasksApi>();
        tasks.Setup(t => t.GetTaskSnapshotAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        tasks.Setup(t => t.GetStoryForTaskAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(story);
        tasks.Setup(t => t.GetConversationMessagesAsync(task.Id, It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>(), It.IsAny<DateTimeOffset?>()))
            .ReturnsAsync([]);

        var services = new ServiceCollection()
            .AddSingleton(tasks.Object)
            .AddScoped<ITaskStoryContextSource, TaskStoryContextSource>()
            .BuildServiceProvider();
        var builder = new TaskChatContextBuilder(tasks.Object, services, NullLogger<TaskChatContextBuilder>.Instance);

        var prompt = await builder.BuildAsync(task, "api", CancellationToken.None);

        prompt.Should().Contain("## Story context — SHOP-10")
            .And.Contain("✓ 1. [SHOP-11] Cart")
            .And.Contain("▶ 2.")
            .And.Contain("Approved plan: Add the cart");
    }
}
