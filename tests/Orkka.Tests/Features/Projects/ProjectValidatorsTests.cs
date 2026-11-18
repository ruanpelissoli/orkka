namespace Orkka.Tests.Features.Projects;

using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Projects.Domain;
using FluentValidation.TestHelper;

public sealed class ProjectValidatorsTests
{
    private readonly CreateProjectValidator _createValidator = new();
    private readonly UpdateProjectValidator _updateValidator = new();

    [Fact]
    public void Create_ValidInput_Passes()
    {
        var request = new CreateProjectRequest("My Project", "A description");
        var result = _createValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Create_NullDescription_Passes()
    {
        var request = new CreateProjectRequest("My Project", null);
        var result = _createValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Create_EmptyName_Fails()
    {
        var request = new CreateProjectRequest("", "desc");
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Create_NameTooLong_Fails()
    {
        var request = new CreateProjectRequest(new string('a', 201), "desc");
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Create_DescriptionTooLong_Fails()
    {
        var request = new CreateProjectRequest("Project", new string('a', 2001));
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Update_AllNull_Passes()
    {
        var request = new UpdateProjectRequest(null, null);
        var result = _updateValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Update_ValidName_Passes()
    {
        var request = new UpdateProjectRequest("New Name", null);
        var result = _updateValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Update_EmptyName_Fails()
    {
        var request = new UpdateProjectRequest("", null);
        var result = _updateValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Update_NameTooLong_Fails()
    {
        var request = new UpdateProjectRequest(new string('a', 201), null);
        var result = _updateValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Update_DescriptionTooLong_Fails()
    {
        var request = new UpdateProjectRequest(null, new string('a', 2001));
        var result = _updateValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    private static UpdateProjectRequest WithRuntimeLimit(bool enabled, int minutes) =>
        new(null, null, Settings: new ProjectSettingsDto(
            MaxParallelTasks: 1,
            SameRepoParallelism: false,
            TaskRuntimeLimitEnabled: enabled,
            TaskRuntimeLimitMinutes: minutes));

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(1440)]
    public void Update_RuntimeLimitInRange_Passes(int minutes)
    {
        var result = _updateValidator.TestValidate(WithRuntimeLimit(enabled: true, minutes));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1441)]
    public void Update_RuntimeLimitOutOfRange_Fails(int minutes)
    {
        var result = _updateValidator.TestValidate(WithRuntimeLimit(enabled: true, minutes));
        result.ShouldHaveValidationErrorFor(x => x.Settings!.TaskRuntimeLimitMinutes);
    }

    [Fact]
    public void Update_RuntimeLimitOff_IgnoresTheMinutesValue()
    {
        // The box keeps its last value while the toggle is off; nothing reads it, so a
        // nonsense number there must not block an unrelated settings save.
        var result = _updateValidator.TestValidate(WithRuntimeLimit(enabled: false, minutes: 0));
        result.ShouldNotHaveAnyValidationErrors();
    }
}
