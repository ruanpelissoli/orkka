namespace Orkka.Modules.Execution.Agents;

/// <summary>
/// Resolves where the user's Ollama server lives. Precedence: ORKKA_OLLAMA_URL (full URL,
/// Orkka-specific escape hatch), then OLLAMA_HOST (Ollama's own convention — accepts
/// "host", "host:port", or a full URL), then the stock local default. Read from the
/// process environment at call time so the runner picks up whatever the user configured
/// for Ollama itself — no separate Orkka config to keep in sync.
/// </summary>
public static class OllamaEnvironment
{
    public const string DefaultBaseUrl = "http://localhost:11434";

    public static string BaseUrl => Resolve(
        Environment.GetEnvironmentVariable("ORKKA_OLLAMA_URL"),
        Environment.GetEnvironmentVariable("OLLAMA_HOST"));

    internal static string Resolve(string? explicitUrl, string? ollamaHost)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return explicitUrl.Trim().TrimEnd('/');

        if (string.IsNullOrWhiteSpace(ollamaHost))
            return DefaultBaseUrl;

        var host = ollamaHost.Trim().TrimEnd('/');
        if (!host.Contains("://"))
            host = "http://" + host;

        // OLLAMA_HOST without an explicit port means Ollama's default port, not the
        // scheme default — checked textually because Uri can't distinguish an explicit
        // ":443" from an implied one.
        var authority = host[(host.IndexOf("://", StringComparison.Ordinal) + 3)..];
        return authority.Contains(':') ? host : $"{host}:11434";
    }
}
