namespace Orkka.Tests.Features.Projects;

using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Projects.Domain;
using Orkka.Modules.Tasks.Domain;
using FluentAssertions;

public sealed class ProjectMappersTests
{
    [Fact]
    public void DefaultSettings_HasExpectedValues()
    {
        Mappers.DefaultSettings.MaxParallelTasks.Should().Be(1);
        Mappers.DefaultSettings.SameRepoParallelism.Should().BeFalse();
        // Developers bringing their own repos confirm merges themselves unless they opt in.
        Mappers.DefaultSettings.AutoApproveCodeReview.Should().BeFalse();
    }

    [Fact]
    public void ToResponse_WithNullSettings_ReturnsDefaults()
    {
        var entity = CreateProject(settings: null);

        var response = entity.ToResponse();

        response.Settings.Should().BeEquivalentTo(Mappers.DefaultSettings);
    }

    [Fact]
    public void ToResponse_IncludesTaskCount()
    {
        var entity = CreateProject();
        var response = entity.ToResponse(taskCount: 42);
        response.TaskCount.Should().Be(42);
    }

    [Fact]
    public void ToResponse_MapsAllFields()
    {
        var entity = CreateProject();
        var response = entity.ToResponse(taskCount: 5);

        response.Id.Should().Be(entity.Id);
        response.OrgId.Should().Be(entity.OrgId);
        response.Name.Should().Be(entity.Name);
        response.Slug.Should().Be(entity.Slug);
        response.Key.Should().Be(entity.Key);
        response.Description.Should().Be(entity.Description);
        response.CreatedAt.Should().Be(entity.CreatedAt);
        response.UpdatedAt.Should().Be(entity.UpdatedAt);
        response.TaskCount.Should().Be(5);
    }

    private static Project CreateProject(string? settings = null) => new()
    {
        Id = Ulid.NewUlid(),
        OrgId = Ulid.NewUlid(),
        Name = "Test Project",
        Slug = "test-project",
        Key = "TP",
        Description = "Test description",
        Settings = settings,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
