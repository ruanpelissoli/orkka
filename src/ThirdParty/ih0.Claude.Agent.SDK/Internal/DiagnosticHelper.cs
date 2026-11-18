using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ih0.Claude.Agent.SDK.Internal;

internal static class DiagnosticHelper
{
    /// <summary>
    /// [orkka patch] An always-on warning channel — the SDK has no logger, and some states are
    /// invisible from the host: a tool call that arrives after the input stream closed is
    /// reported to the user as "the tool was interrupted", with nothing anywhere saying why.
    /// stderr of the process hosting the SDK (the runner, the Worker) is where that belongs.
    /// </summary>
    public static void Warn(string message) =>
        Console.Error.WriteLine($"[ClaudeAgentSdk] {message}");

    [Conditional("DEBUG")]
    public static void LogIgnoredException(Exception ex, [CallerMemberName] string? caller = null)
    {
        Debug.WriteLine($"[ClaudeAgentSdk:{caller}] Ignored {ex.GetType().Name}: {ex.Message}");
    }
}
