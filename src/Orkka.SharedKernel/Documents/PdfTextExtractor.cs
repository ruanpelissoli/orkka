namespace Orkka.SharedKernel.Documents;

using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

/// <summary>Result of extracting a PDF's text. <see cref="Text"/> is null when nothing usable
/// came out — <see cref="Failure"/> then says why (encrypted, malformed, or image-only).</summary>
public sealed record PdfTextResult(string? Text, int PageCount, bool Truncated, string? Failure)
{
    public static PdfTextResult Failed(string reason) => new(null, 0, false, reason);
}

/// <summary>
/// Pulls the text layer out of a PDF in-process, so agents never have to shell out to
/// pdftotext (not guaranteed to exist in a worktree or sandbox) and never have to open a
/// PDF with the Read tool (which kills the run — see AgentPdfReadGuard).
///
/// Text-layer only: a scanned/photographed PDF has no text to extract and comes back as a
/// failure rather than an empty file. There is no OCR here.
/// </summary>
public static class PdfTextExtractor
{
    /// <summary>Upper bound on extracted characters (~50k tokens). Anything past this is
    /// cut with a marker — an agent that needs more should ask for a narrower document.</summary>
    public const int DefaultMaxChars = 200_000;

    public static PdfTextResult Extract(byte[] pdfBytes, int maxChars = DefaultMaxChars)
    {
        if (pdfBytes.Length == 0)
            return PdfTextResult.Failed("the file is empty");

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(pdfBytes, new ParsingOptions { UseLenientParsing = true });
        }
        catch (Exception ex)
        {
            // Encrypted, truncated, or not a PDF at all. Extraction is best-effort context
            // for a prompt — never let it surface as an exception to the caller.
            return PdfTextResult.Failed(Describe(ex));
        }

        using (document)
        {
            var sb = new StringBuilder();
            var truncated = false;
            var pageCount = document.NumberOfPages;

            for (var number = 1; number <= pageCount; number++)
            {
                var text = ReadPage(document, number);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var chunk = $"\n=== page {number} ===\n{text.Trim()}\n";
                var remaining = maxChars - sb.Length;
                if (chunk.Length > remaining)
                {
                    if (remaining > 0)
                        sb.Append(chunk.AsSpan(0, remaining));
                    truncated = true;
                    break;
                }

                sb.Append(chunk);
            }

            if (sb.Length == 0)
                return PdfTextResult.Failed(
                    "no extractable text — the PDF is image-only (scanned or exported as pictures)");

            return new PdfTextResult(sb.ToString(), pageCount, truncated, null);
        }
    }

    /// <summary>One malformed page (broken font, damaged content stream) must not cost us
    /// the other 40 — every page is read on its own.</summary>
    private static string? ReadPage(PdfDocument document, int number)
    {
        try
        {
            var page = document.GetPage(number);
            var text = ContentOrderTextExtractor.GetText(page);
            return string.IsNullOrWhiteSpace(text) ? FallbackText(page) : Normalize(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Content-order extraction returns nothing on some generators' pages even though
    /// letters are present; the raw letter concatenation still beats an empty file.</summary>
    private static string? FallbackText(Page page)
    {
        var text = page.Text;
        return string.IsNullOrWhiteSpace(text) ? null : Normalize(text);
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Describe(Exception ex) =>
        ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
}
