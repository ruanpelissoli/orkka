namespace Orkka.Tests.Utilities;

using System.Text;
using FluentAssertions;
using Orkka.SharedKernel.Documents;

public sealed class PdfTextExtractorTests
{
    [Fact]
    public void Extract_ReturnsTheTextLayer()
    {
        var pdf = TestPdfs.SinglePage("Acceptance criteria", "Latency under 200ms");

        var result = PdfTextExtractor.Extract(pdf);

        result.Failure.Should().BeNull();
        result.PageCount.Should().Be(1);
        result.Truncated.Should().BeFalse();
        result.Text.Should().Contain("Acceptance criteria");
        result.Text.Should().Contain("Latency under 200ms");
    }

    [Fact]
    public void Extract_LabelsEachPage()
    {
        var pdf = TestPdfs.Pages("Introduction", "Requirements", "Appendix");

        var result = PdfTextExtractor.Extract(pdf);

        result.PageCount.Should().Be(3);
        result.Text.Should().Contain("=== page 1 ===")
            .And.Contain("=== page 2 ===")
            .And.Contain("=== page 3 ===");
        result.Text!.IndexOf("Requirements", StringComparison.Ordinal)
            .Should().BeGreaterThan(result.Text.IndexOf("Introduction", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_StopsAtTheCharacterCap()
    {
        // The cap is what keeps a 400-page PDF from swallowing the agent's context window.
        var pdf = TestPdfs.Pages("Introduction", "Requirements", "Appendix");

        var result = PdfTextExtractor.Extract(pdf, maxChars: 40);

        result.Truncated.Should().BeTrue();
        result.Text.Should().NotBeNull();
        result.Text!.Length.Should().BeLessThanOrEqualTo(40);
    }

    [Fact]
    public void Extract_ReportsImageOnlyPdfsInsteadOfReturningNothing()
    {
        // A scanned PDF has no text layer — the caller must be able to tell the agent that,
        // rather than handing it an empty file it would read as "the spec is blank".
        var result = PdfTextExtractor.Extract(TestPdfs.ImageOnly());

        result.Text.Should().BeNull();
        result.Failure.Should().Contain("image-only");
    }

    [Fact]
    public void Extract_ReportsMalformedFilesWithoutThrowing()
    {
        // Prompt building calls this — an exception here would block the whole task.
        var result = PdfTextExtractor.Extract(Encoding.ASCII.GetBytes("this is not a PDF"));

        result.Text.Should().BeNull();
        result.Failure.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Extract_ReportsEmptyFiles()
    {
        var result = PdfTextExtractor.Extract([]);

        result.Text.Should().BeNull();
        result.Failure.Should().Contain("empty");
    }
}
