namespace Orkka.Modules.Execution.Git;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// Ends the processes that keep a finished task's worktree from being deleted (Windows only).
///
/// An agent can start a dev server, a test watcher or a bundler service in the background; on
/// Windows those outlive the CLI that started them and hold files inside the worktree's
/// node_modules. Every merged task's folder then survived its cleanup (prod runner,
/// 2026-09-17: "The process cannot access the file because it is being used by another
/// process" for each storefront-ui worktree).
///
/// Deliberately narrow, because this runs on a person's own machine:
/// <list type="bullet">
/// <item>any process whose EXECUTABLE lives inside the worktree (esbuild.exe, a native bundler
/// binary in node_modules) — nothing a person opened lives there;</item>
/// <item>a script runtime (node, bun, deno) whose command line names a path inside this exact
/// worktree — the dev server or watcher itself.</item>
/// </list>
/// Never an editor, shell or anything else that merely mentions the folder — "Open in editor"
/// starts one with the worktree on its command line. Every process ended is logged by name,
/// so the next report names the culprit.
/// </summary>
internal static class WorktreeProcessSweeper
{
    internal static readonly string[] ScriptRuntimes = ["node.exe", "bun.exe", "deno.exe"];

    internal sealed record ProcessInfo(int ProcessId, string? Name, string? ExecutablePath, string? CommandLine);

    private const string ListScript =
        "Get-CimInstance Win32_Process | Select-Object ProcessId,Name,ExecutablePath,CommandLine | ConvertTo-Json -Compress";

    public static async Task<IReadOnlyList<ProcessInfo>> EndHoldersAsync(
        string worktreePath, ILogger logger, CancellationToken ct)
    {
        var ended = new List<ProcessInfo>();
        try
        {
            var processes = await ListProcessesAsync(ct);
            var self = Environment.ProcessId;
            foreach (var process in processes.Where(p => ShouldEnd(p, worktreePath, self)))
            {
                try
                {
                    using var handle = Process.GetProcessById(process.ProcessId);
                    handle.Kill(entireProcessTree: true);
                    ended.Add(process);
                    logger.LogWarning(
                        "Ended {Name} (pid {Pid}) — it was still holding the finished worktree {Path}: {CommandLine}",
                        process.Name, process.ProcessId, worktreePath, Truncate(process.CommandLine, 300));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Gone already, or not ours to end — the delete retry says which.
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not list the processes holding worktree {Path}", worktreePath);
        }
        return ended;
    }

    /// <summary>The whole rule, pure, so it can be pinned by tests.</summary>
    internal static bool ShouldEnd(ProcessInfo process, string worktreePath, int selfProcessId)
    {
        if (process.ProcessId == selfProcessId || process.ProcessId <= 4)
            return false;

        var root = Normalize(worktreePath);
        if (process.ExecutablePath is { Length: > 0 } exe && IsInside(Normalize(exe), root))
            return true;

        var name = process.Name ?? "";
        return ScriptRuntimes.Contains(name, StringComparer.OrdinalIgnoreCase)
            && process.CommandLine is { Length: > 0 } commandLine
            && MentionsPathInside(commandLine, root);
    }

    internal static IReadOnlyList<ProcessInfo> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.EnumerateArray().ToList(),
            JsonValueKind.Object => [doc.RootElement],
            _ => [],
        };
        return items
            .Where(e => e.TryGetProperty("ProcessId", out var id) && id.ValueKind == JsonValueKind.Number)
            .Select(e => new ProcessInfo(
                e.GetProperty("ProcessId").GetInt32(),
                Str(e, "Name"), Str(e, "ExecutablePath"), Str(e, "CommandLine")))
            .ToList();
    }

    private static async Task<IReadOnlyList<ProcessInfo>> ListProcessesAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", ListScript })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("powershell.exe could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return [];
        }
        return Parse(await stdout);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Normalize(string path)
        => path.Replace('/', '\\').TrimEnd('\\');

    private static bool IsInside(string path, string root)
        => path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The root appears in the command line as a path of its own — followed by a
    /// separator, a quote, whitespace or the end — so STO-3 never matches STO-35.</summary>
    private static bool MentionsPathInside(string commandLine, string root)
    {
        var text = commandLine.Replace('/', '\\');
        var index = 0;
        while ((index = text.IndexOf(root, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = index + root.Length;
            if (end == text.Length || text[end] is '\\' or '"' or '\'' or ' ' or '\t')
                return true;
            index = end;
        }
        return false;
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max] + "…";
}
