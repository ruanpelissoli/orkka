namespace Orkka.Tests.Utilities;

using System.Text;

/// <summary>
/// Builds real (if minimal) PDF files so the extractor is exercised against actual PDF
/// syntax — cross-reference table, page tree, content streams — instead of a mock.
/// Helvetica is one of the standard 14 fonts, so no font file has to be embedded.
/// </summary>
internal static class TestPdfs
{
    /// <summary>One page carrying the given lines of text.</summary>
    public static byte[] SinglePage(params string[] lines) => Build([TextStream(lines)]);

    /// <summary>One page per argument, each carrying that single line of text.</summary>
    public static byte[] Pages(params string[] pageLines) =>
        Build([.. pageLines.Select(line => TextStream([line]))]);

    /// <summary>A page with no text operators at all — the shape a scanned PDF has once the
    /// (absent) image is ignored.</summary>
    public static byte[] ImageOnly() => Build([""]);

    private static string TextStream(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder("BT\n/F1 12 Tf\n");
        var y = 720;
        foreach (var line in lines)
        {
            sb.Append($"1 0 0 1 72 {y} Tm\n({Escape(line)}) Tj\n");
            y -= 18;
        }
        return sb.Append("ET\n").ToString();
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static byte[] Build(IReadOnlyList<string> pageContents)
    {
        // Object numbering: 1 catalog, 2 page tree, 3 font, then (page, content) pairs.
        var kids = string.Join(" ", pageContents.Select((_, i) => $"{4 + i * 2} 0 R"));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pageContents.Count} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        for (var i = 0; i < pageContents.Count; i++)
        {
            objects.Add(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            objects.Add(
                $"<< /Length {Encoding.ASCII.GetByteCount(pageContents[i])} >>\n" +
                $"stream\n{pageContents[i]}\nendstream");
        }

        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");

        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        // Every xref entry is exactly 20 bytes — readers seek by fixed stride.
        var startXref = ms.Position;
        Write($"xref\n0 {objects.Count + 1}\n");
        Write("0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{startXref}\n%%EOF");

        return ms.ToArray();
    }
}
