namespace Orkka.Tests.Features.CodeReview;

using Orkka.Modules.Tasks.Features.CodeReview;
using FluentAssertions;
using FluentValidation.TestHelper;

public sealed class CodeReviewTests
{
    // --- SubmitCodeReviewValidator ---

    private readonly SubmitCodeReviewValidator _submitValidator = new();
    private readonly AddLineCommentValidator _commentValidator = new();

    [Theory]
    [InlineData("approve")]
    [InlineData("request_changes")]
    [InlineData("comment")]
    [InlineData("APPROVE")]
    [InlineData("REQUEST_CHANGES")]
    [InlineData("Comment")]
    public void SubmitReview_ValidDecisions_Pass(string decision)
    {
        var request = new SubmitCodeReviewRequest(decision, "Some body", null);
        var result = _submitValidator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Decision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("merge")]
    [InlineData("reject")]
    [InlineData("invalid")]
    public void SubmitReview_InvalidDecisions_Fail(string decision)
    {
        var request = new SubmitCodeReviewRequest(decision, "body", null);
        var result = _submitValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Decision);
    }

    [Fact]
    public void SubmitReview_RequestChangesWithoutBody_Fails()
    {
        var request = new SubmitCodeReviewRequest("request_changes", null, null);
        var result = _submitValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Body);
    }

    [Fact]
    public void SubmitReview_RequestChangesWithBody_Passes()
    {
        var request = new SubmitCodeReviewRequest("request_changes", "Fix the bug", null);
        var result = _submitValidator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.Body);
    }

    [Fact]
    public void SubmitReview_ApproveWithoutBody_Passes()
    {
        var request = new SubmitCodeReviewRequest("approve", null, null);
        var result = _submitValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void SubmitReview_BodyTooLong_Fails()
    {
        var request = new SubmitCodeReviewRequest("approve", new string('a', 10001), null);
        var result = _submitValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Body);
    }

    // --- AddLineCommentValidator ---

    [Fact]
    public void AddComment_ValidInput_Passes()
    {
        var request = new AddLineCommentRequest("src/File.cs", 42, "Fix this line");
        var result = _commentValidator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void AddComment_EmptyPath_Fails()
    {
        var request = new AddLineCommentRequest("", 42, "Fix this");
        var result = _commentValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Path);
    }

    [Fact]
    public void AddComment_ZeroLine_Fails()
    {
        var request = new AddLineCommentRequest("src/File.cs", 0, "Fix this");
        var result = _commentValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Line);
    }

    [Fact]
    public void AddComment_EmptyBody_Fails()
    {
        var request = new AddLineCommentRequest("src/File.cs", 42, "");
        var result = _commentValidator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Body);
    }

    // --- CodeReviewHelpers ---

    [Theory]
    [InlineData("https://github.com/user/repo/pull/42", 42)]
    [InlineData("https://github.com/org/project/pull/1", 1)]
    [InlineData("https://github.com/org/project/pull/999", 999)]
    public void ParsePrNumber_ValidUrls_ReturnsNumber(string url, int expected)
    {
        var result = CodeReviewHelpers.ParsePrNumber(url);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://github.com/user/repo")]
    [InlineData("not-a-url")]
    [InlineData("https://github.com/user/repo/pull/abc")]
    public void ParsePrNumber_InvalidUrls_ReturnsNull(string url)
    {
        var result = CodeReviewHelpers.ParsePrNumber(url);
        result.Should().BeNull();
    }

}
