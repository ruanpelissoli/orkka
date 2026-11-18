namespace Orkka.SharedKernel.Ai;

using System.Text.Json;

/// <summary>
/// Reads the MCP server roster the Claude Code CLI reports in its <c>system</c>/<c>init</c>
/// event (<c>mcp_servers: [{ name, status }]</c>, status one of <c>connected</c>,
/// <c>failed</c>, <c>needs-auth</c>, <c>pending</c>, <c>disabled</c>). This is the only
/// host-side signal that a server — in particular an SDK-hosted in-process tool server —
/// did not come up: the CLI writes the connect error solely to its own mcp-logs file and the
/// run proceeds without the tools. Pure parsing, no SDK types, so both the pipeline invoker
/// and the chat service can share it.
/// </summary>
public static class ClaudeMcpInitStatus
{
    public const string Connected = "connected";

    /// <summary>Status a server registered for the run gets when the roster omits it entirely.</summary>
    public const string Missing = "missing";

    public sealed record McpServerStatus(string Name, string Status);

    /// <summary>Every server in the init payload's <c>mcp_servers</c> array, in order.
    /// Empty when the payload has no such array (older CLI, non-init event).</summary>
    public static IReadOnlyList<McpServerStatus> Read(JsonElement initData)
    {
        if (!TryGetRoster(initData, out var servers))
            return [];

        var list = new List<McpServerStatus>();
        foreach (var server in servers.EnumerateArray())
        {
            if (server.ValueKind != JsonValueKind.Object)
                continue;

            var name = server.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()
                : null;
            if (string.IsNullOrEmpty(name))
                continue;

            var status = server.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString() ?? ""
                : "";
            list.Add(new McpServerStatus(name, status));
        }
        return list;
    }

    /// <summary>
    /// The servers in <paramref name="expectedServers"/> that did not connect: reported with any
    /// status other than <see cref="Connected"/>, or absent from the roster (<see cref="Missing"/>).
    /// Empty when the payload carries no roster at all — no roster is no evidence, not a failure.
    /// </summary>
    public static IReadOnlyList<McpServerStatus> Unavailable(
        JsonElement initData, IEnumerable<string> expectedServers)
    {
        if (!TryGetRoster(initData, out _))
            return [];

        var reported = Read(initData);
        var result = new List<McpServerStatus>();
        foreach (var name in expectedServers.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal))
        {
            var match = reported.FirstOrDefault(r => r.Name == name);
            if (match is null)
                result.Add(new McpServerStatus(name, Missing));
            else if (!string.Equals(match.Status, Connected, StringComparison.OrdinalIgnoreCase))
                result.Add(match);
        }
        return result;
    }

    private static bool TryGetRoster(JsonElement initData, out JsonElement servers)
    {
        servers = default;
        return initData.ValueKind == JsonValueKind.Object
            && initData.TryGetProperty("mcp_servers", out servers)
            && servers.ValueKind == JsonValueKind.Array;
    }
}
