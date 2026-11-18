namespace Orkka.Tests.Features.Tasks;

using Orkka.Modules.Tasks.Features.Tasks;
using FluentValidation.TestHelper;

public sealed class TaskValidatorsTests
{
    private readonly CreateTaskValidator _createValidator = new();
    private readonly UpdateTaskValidator _updateValidator = new();
    private readonly MoveTaskLaneValidator _moveValidator = new();


    // CreateTask

    [Fact]
    public void Create_ValidInput_Passes()
    {
        var request = new CreateTaskRequest(Ulid.NewUlid(), "Fix bug", "Description");
        var result = _createValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Create_EmptyTitle_Fails()
    {
        var request = new CreateTaskRequest(Ulid.NewUlid(), "", "desc");
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Create_TitleTooLong_Fails()
    {
        var request = new CreateTaskRequest(Ulid.NewUlid(), new string('a', 501), "desc");
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Create_DescriptionTooLong_Fails()
    {
        var request = new CreateTaskRequest(Ulid.NewUlid(), "Title", new string('a', 10001));
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Create_InvalidPriority_Fails()
    {
        var request = new CreateTaskRequest(Ulid.NewUlid(), "Title", "desc", "Urgent");
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Priority);
    }

    [Fact]
    public void Create_EmptyRepoId_Fails()
    {
        var request = new CreateTaskRequest(Ulid.Empty, "Title", "desc");
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.RepoId);
    }

    [Theory]
    [InlineData("Low")]
    [InlineData("Medium")]
    [InlineData("High")]
    [InlineData("Critical")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("critical")]
    public void Create_ValidPriority_Passes(string priority)
    {
        var request = new CreateTaskRequest(Ulid.NewUlid(), "Title", "desc", priority);
        var result = _createValidator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Priority);
    }

    [Fact]
    public void Create_InvalidType_Fails()
    {
        var request = new CreateTaskRequest(Ulid.NewUlid(), "Title", "desc", "Medium", "Task");
        var result = _createValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Type);
    }

    [Theory]
    [InlineData("Feature")]
    [InlineData("BugFix")]
    [InlineData("Refactor")]
    [InlineData("Improvement")]
    [InlineData("feature")]
    [InlineData("bugfix")]
    [InlineData("refactor")]
    [InlineData("improvement")]
    public void Create_ValidType_Passes(string type)
    {
        var request = new CreateTaskRequest(Ulid.NewUlid(), "Title", "desc", "Medium", type);
        var result = _createValidator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Type);
    }

    // UpdateTask

    [Fact]
    public void Update_AllNull_Passes()
    {
        var request = new UpdateTaskRequest(null, null, null, null, null, null);
        var result = _updateValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Update_TitleTooLong_Fails()
    {
        var request = new UpdateTaskRequest(new string('a', 501), null, null, null, null, null);
        var result = _updateValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Update_InvalidPriority_Fails()
    {
        var request = new UpdateTaskRequest(null, null, "Urgent", null, null, null);
        var result = _updateValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Priority);
    }

    [Fact]
    public void Update_InvalidType_Fails()
    {
        var request = new UpdateTaskRequest(null, null, null, "Task", null, null);
        var result = _updateValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Type);
    }

    [Fact]
    public void Update_ValidType_Passes()
    {
        var request = new UpdateTaskRequest(null, null, null, "BugFix", null, null);
        var result = _updateValidator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Type);
    }

    // MoveLane

    [Fact]
    public void MoveLane_EmptyTarget_Fails()
    {
        var request = new MoveTaskLaneRequest("");
        var result = _moveValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.TargetLane);
    }

    [Fact]
    public void MoveLane_ValidTarget_Passes()
    {
        var request = new MoveTaskLaneRequest("Ready");
        var result = _moveValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

}
