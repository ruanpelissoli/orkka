namespace Orkka.Modules.Documents.Infrastructure.Services;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using HtmlToOpenXml;
using Markdig;

/// <summary>Renders a markdown document to .docx: markdown → HTML via Markdig, HTML →
/// OpenXML via HtmlToOpenXml. Kept behind this service so the engine can be swapped
/// without touching the endpoint — same seam pattern as <see cref="PdfExportService"/>.</summary>
public sealed class DocxExportService
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public async Task<byte[]> RenderAsync(string title, string markdown, CancellationToken ct = default)
    {
        var html = Markdown.ToHtml(markdown, Pipeline);
        var titleHtml = $"<h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>";

        using var stream = new MemoryStream();
        using (var wordDocument = WordprocessingDocument.Create(
            stream, WordprocessingDocumentType.Document))
        {
            var mainPart = wordDocument.AddMainDocumentPart();
            mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new DocumentFormat.OpenXml.Wordprocessing.Body());

            var converter = new HtmlConverter(mainPart);
            await converter.ParseBody(titleHtml + html, ct);

            mainPart.Document.Save();
        }

        return stream.ToArray();
    }
}
