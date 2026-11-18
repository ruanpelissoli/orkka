namespace Orkka.Modules.Execution.Orchestration;

using System.Text;

/// <summary>
/// Renders the "Task Attachments" prompt section. Split out of <see cref="TaskOrchestrator"/>
/// so the wording — especially the PDF instructions, which decide whether an agent reaches for
/// a file it cannot open — is directly testable; the orchestrator does the storage I/O and
/// hands the finished lines over.
/// </summary>
internal static class TaskAttachmentPrompt
{
    internal const string DownloadDirectory = ".orkka/context/attachments";

    /// <summary>One attachment, already resolved to signed URLs. <paramref name="TextUrl"/> is
    /// set only for PDFs whose text Orkka managed to extract.</summary>
    internal sealed record Line(
        string FileName,
        string FileType,
        long FileSize,
        string SafeName,
        string DownloadUrl,
        bool IsPdf,
        string? TextUrl);

    public static string Render(IReadOnlyList<Line> lines, int omittedCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Task Attachments");
        sb.AppendLine("The user uploaded these files to this task — they are NOT in the repository yet. " +
                      "When the task references an attached file (sketch, asset, spec, screenshot), " +
                      "download it with the matching command below. Download into .orkka/context/ to " +
                      "inspect it (images can then be viewed with the Read tool), or directly to its " +
                      "destination when the task asks for the file to be added to the project. NEVER " +
                      "commit or hard-code the download URL itself — it expires.");

        if (lines.Any(l => l.IsPdf))
        {
            sb.AppendLine();
            sb.AppendLine("PDFs work differently: the Read tool cannot open a PDF here. Orkka already " +
                          "extracted each PDF's text server-side and published it as a .txt file — " +
                          "download that and Read it. Do NOT run pdftotext or any other extractor, and " +
                          "do NOT Read the .pdf itself.");
        }

        sb.AppendLine();

        foreach (var line in lines)
        {
            sb.AppendLine($"- {line.FileName} ({line.FileType}, {line.FileSize:N0} bytes):");

            if (line is { IsPdf: true, TextUrl: not null })
            {
                sb.AppendLine("  Extracted text — download and Read this one:");
                sb.AppendLine(Curl($"{line.SafeName}.txt", line.TextUrl));
                sb.AppendLine("  Original PDF (only if the task asks for the file itself — never Read it):");
            }
            else if (line.IsPdf)
            {
                sb.AppendLine("  No text could be extracted (scanned or image-only PDF) — say so rather " +
                              "than trying to Read it. The file itself:");
            }

            sb.AppendLine(Curl(line.SafeName, line.DownloadUrl));
        }

        if (omittedCount > 0)
            sb.AppendLine($"_(+{omittedCount} more attachments omitted)_");

        return sb.ToString();
    }

    private static string Curl(string fileName, string url) =>
        $"  curl -L --create-dirs -o \"{DownloadDirectory}/{fileName}\" \"{url}\"";
}
