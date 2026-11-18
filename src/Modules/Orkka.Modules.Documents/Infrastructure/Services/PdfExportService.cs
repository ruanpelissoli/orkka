namespace Orkka.Modules.Documents.Infrastructure.Services;

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Markdown;

/// <summary>Renders a markdown document to PDF. Kept behind this service so the markdown→PDF
/// engine (currently the QuestPDF.Markdown community package) can be swapped without touching
/// the endpoint.</summary>
public sealed class PdfExportService
{
    public byte[] Render(string title, string markdown)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(10));

                page.Header()
                    .PaddingBottom(0.5f, Unit.Centimetre)
                    .Text(title)
                    .FontSize(20)
                    .SemiBold();

                page.Content().Markdown(markdown);

                page.Footer()
                    .AlignCenter()
                    .Text(t =>
                    {
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
            });
        });

        return document.GeneratePdf();
    }
}
