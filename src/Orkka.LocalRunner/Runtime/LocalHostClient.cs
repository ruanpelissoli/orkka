namespace Orkka.LocalRunner.Runtime;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>One workspace the local host knows. <see cref="Token"/> is an <c>orkr_</c> runner
/// token, present only when the bootstrap call just minted it.</summary>
public sealed record BootstrapWorkspace
{
    public required string WorkspaceId { get; init; }
    public required string Name { get; init; }
    public string? Token { get; init; }

    public override string ToString() => $"{Name} ({WorkspaceId}{(Token is null ? "" : ", token hidden")})";
}

/// <summary>
/// The CLI's calls to the running Orkka host, all to 127.0.0.1 with the install secret as the
/// bearer: health, launch codes for the browser, the runner bootstrap, shutdown. No HTTP proxy
/// is ever used.
/// </summary>
public sealed class LocalHostClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string? _installSecret;

    public LocalHostClient(int hostPort, string? installSecret, HttpMessageHandler? handler = null)
    {
        HostPort = hostPort;
        _ownsClient = true;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{hostPort}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        // Sent only on the /local/* calls.
        _installSecret = installSecret;
    }

    public int HostPort { get; }

    public string BaseUrl => $"http://127.0.0.1:{HostPort}";

    public static string LaunchUrl(int hostPort, string code) =>
        $"http://127.0.0.1:{hostPort}/launch?code={Uri.EscapeDataString(code)}";

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.GetAsync("health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>True when an Orkka host answers <c>/health</c> on the port (its JSON names the
    /// service) — how <c>./orkka start</c> tells "Orkka already runs here" from "some other
    /// program holds the port".</summary>
    public async Task<bool> IsOrkkaAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.GetAsync("health", cts.Token);
            if (!response.IsSuccessStatusCode)
                return false;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            return doc.RootElement.TryGetProperty("service", out var service)
                && service.GetString() == "orkka";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    public async Task<string> CreateLaunchCodeAsync(CancellationToken ct)
    {
        using var response = await PostLocalAsync("local/launch-codes", ct);
        await EnsureOkAsync(response, "mint a launch code", ct);
        var body = await response.Content.ReadFromJsonAsync<LaunchCodeResponse>(Json, ct);
        return body?.Code is { Length: > 0 } code
            ? code
            : throw new InvalidOperationException("The host answered without a launch code.");
    }

    public async Task<IReadOnlyList<BootstrapWorkspace>> BootstrapAsync(CancellationToken ct)
    {
        using var response = await PostLocalAsync("local/bootstrap", ct);
        await EnsureOkAsync(response, "bootstrap the runner", ct);
        return await response.Content.ReadFromJsonAsync<List<BootstrapWorkspace>>(Json, ct) ?? [];
    }

    public async Task<bool> ShutdownAsync(CancellationToken ct)
    {
        try
        {
            using var response = await PostLocalAsync("local/shutdown", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private Task<HttpResponseMessage> PostLocalAsync(string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (!string.IsNullOrEmpty(_installSecret))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _installSecret);
        return _http.SendAsync(request, ct);
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        if (detail.Length > 300)
            detail = detail[..300];
        throw new InvalidOperationException($"The local host refused to {what} ({(int)response.StatusCode}): {detail}");
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }

    private sealed record LaunchCodeResponse([property: JsonPropertyName("code")] string? Code);
}
