namespace Orkka.Modules.Execution.Agents;

using System.Text.Json;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Pre-execution hook that stops the agent from opening a PDF with the Read tool.
///
/// The CLI answers a PDF read by streaming the file back as a top-level <c>document</c>
/// content block, and the SDK's message parser only knows text/thinking/tool_use/
/// tool_result — it throws <c>Unknown content block type: document</c>, which kills the
/// whole run mid-stream and blocks the task ("Agent process failed"). Nothing downstream
/// can recover: the exception surfaces from the message iterator itself.
///
/// Denying the read turns a fatal crash into a message the agent can act on. Images are
/// unaffected — the CLI nests those inside the tool_result, which the SDK parses fine.
///
/// The text is not lost: Orkka extracts every PDF attachment server-side and offers it as a
/// .txt sibling in the Task Attachments prompt section, so the deny message points there
/// rather than asking the agent to find an extractor it may not have.
/// </summary>
public sealed class AgentPdfReadGuard(ILogger logger)
{
    private const string DenyReason =
        "PDF files cannot be opened with the Read tool in this environment — the agent " +
        "runtime cannot stream PDF content and the run would fail. Orkka already extracted " +
        "the text of every PDF attachment: read the .txt file next to it instead (same name " +
        "with .txt appended — the Task Attachments section of your prompt has the download " +
        "command). Do not run pdftotext or any other extractor. If this PDF has no .txt " +
        "companion it has no extractable text (scanned or image-only) — say so in your " +
        "response and work from the task description rather than retrying the Read.";

    public Task<HookOutput> EvaluateRead(HookInput input, string? toolUseId, HookContext? context)
    {
        var path = ExtractFilePath(input);

        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Blocked Read of PDF {Path} — the agent runtime cannot parse document blocks", path);
            return Task.FromResult(AgentHookResponses.Deny(DenyReason));
        }

        return Task.FromResult(AgentHookResponses.Allow());
    }

    private static string ExtractFilePath(HookInput input)
    {
        if (input is PreToolUseHookInput preToolUse &&
            preToolUse.ToolInput.ValueKind == JsonValueKind.Object &&
            preToolUse.ToolInput.TryGetProperty("file_path", out var filePath))
        {
            return filePath.GetString() ?? "";
        }

        return "";
    }
}
