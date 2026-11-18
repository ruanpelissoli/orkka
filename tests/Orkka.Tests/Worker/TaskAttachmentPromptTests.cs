namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;

public sealed class TaskAttachmentPromptTests
{
    private static TaskAttachmentPrompt.Line Pdf(string? textUrl) => new(
        "Wave 25 spec.pdf", "application/pdf", 1_234_567, "Wave-25-spec.pdf",
        "https://blobs.test/spec.pdf?sig=abc", IsPdf: true, TextUrl: textUrl);

    private static TaskAttachmentPrompt.Line Image() => new(
        "mockup.png", "image/png", 4_096, "mockup.png",
        "https://blobs.test/mockup.png?sig=abc", IsPdf: false, TextUrl: null);

    [Fact]
    public void Render_PointsPdfsAtTheExtractedText()
    {
        var section = TaskAttachmentPrompt.Render([Pdf("https://blobs.test/spec.pdf.txt?sig=xyz")], omittedCount: 0);

        section.Should().Contain(
            "curl -L --create-dirs -o \".orkka/context/attachments/Wave-25-spec.pdf.txt\" " +
            "\"https://blobs.test/spec.pdf.txt?sig=xyz\"");
        section.Should().Contain("download and Read this one");
        // The PDF itself stays available — a task may ask for the file to be committed —
        // but must never be presented as something to Read.
        section.Should().Contain("never Read it");
        section.Should().Contain("\"https://blobs.test/spec.pdf?sig=abc\"");
    }

    [Fact]
    public void Render_NeverTellsTheAgentToRunAnExtractor()
    {
        // pdftotext is not guaranteed to exist in a worktree or sandbox; the old wording sent
        // agents chasing a binary that isn't there.
        var section = TaskAttachmentPrompt.Render([Pdf("https://blobs.test/spec.pdf.txt?sig=xyz")], omittedCount: 0);

        section.Should().NotContain("pdftotext <file>");
        section.Should().Contain("Do NOT run pdftotext or any other extractor");
    }

    [Fact]
    public void Render_SaysSoWhenAPdfHasNoExtractableText()
    {
        var section = TaskAttachmentPrompt.Render([Pdf(textUrl: null)], omittedCount: 0);

        section.Should().Contain("No text could be extracted (scanned or image-only PDF)");
        section.Should().NotContain(".pdf.txt");
        section.Should().Contain("\"https://blobs.test/spec.pdf?sig=abc\"");
    }

    [Fact]
    public void Render_LeavesNonPdfAttachmentsAlone()
    {
        var section = TaskAttachmentPrompt.Render([Image()], omittedCount: 0);

        section.Should().Contain("- mockup.png (image/png, 4,096 bytes):");
        section.Should().Contain(
            "curl -L --create-dirs -o \".orkka/context/attachments/mockup.png\" " +
            "\"https://blobs.test/mockup.png?sig=abc\"");
        // No PDF in the list — the PDF paragraph is dead weight in the prompt.
        section.Should().NotContain("PDFs work differently");
    }

    [Fact]
    public void Render_NotesOmittedAttachments()
    {
        var section = TaskAttachmentPrompt.Render([Image()], omittedCount: 4);

        section.Should().Contain("_(+4 more attachments omitted)_");
    }
}
