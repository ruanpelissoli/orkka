namespace Orkka.LocalRunner.Trackers;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// What a tracker client authenticates with. Not a record on purpose: a record's generated
/// ToString prints every property, and one interpolated into a log line or an exception
/// would carry the token with it.
/// </summary>
public sealed class TrackerCredential(string siteUrl, string? email, string secret)
{
    public string SiteUrl { get; } = siteUrl.TrimEnd('/');
    public string? Email { get; } = email;
    internal string Secret { get; } = secret;

    /// <summary>The Basic header value: <c>email:token</c> for Jira, <c>:pat</c> for ADO.</summary>
    internal string BasicValue => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{Secret}"));

    /// <summary>Removes the token — and the Basic header built from it — from any text that
    /// is about to leave this machine or reach a terminal.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        if (Secret.Length > 0)
            text = text.Replace(Secret, "***", StringComparison.Ordinal);
        return text.Replace(BasicValue, "***", StringComparison.Ordinal);
    }

    public override string ToString() => $"{SiteUrl} (token hidden)";
}

/// <summary>A tracker said no, in words the user can act on. Messages are built from our own
/// text and the tracker's error body, and are scrubbed before they leave the runner.</summary>
public sealed class TrackerException(string message) : Exception(message);

public sealed record TrackerIdentity(string? AccountId, string? DisplayName);

public sealed record TrackerPreview(int MatchCount, TrackerTicketPreview[] Sample, string EffectiveQuery);

/// <summary>The board a ticket source is limited to: a Jira board id or an Azure DevOps team id,
/// plus the name it was saved with (only for messages).</summary>
public sealed record TrackerBoardRef(string Id, string? Name)
{
    public string Label => string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();

    /// <summary>Null when no board is set — the whole scope.</summary>
    public static TrackerBoardRef? From(string? id, string? name)
        => string.IsNullOrWhiteSpace(id) ? null : new TrackerBoardRef(id.Trim(), name);

    /// <summary>What a poll or a preview says when the board is gone: it fails, and never falls
    /// back to the whole scope.</summary>
    public TrackerException NotFound(string scope)
        => new($"The board '{Label}' was not found in {scope} — pick another board in the ticket source settings.");
}

/// <summary>One tracker's API, behind the <see cref="TrackerQueryKinds"/>.</summary>
public interface ITrackerClient
{
    Task<TrackerIdentity> TestAsync(TrackerCredential credential, CancellationToken ct);
    Task<TrackerScope[]> ScopesAsync(TrackerCredential credential, CancellationToken ct);
    Task<TrackerEpic[]> EpicsAsync(TrackerCredential credential, string scope, string? search, CancellationToken ct);
    Task<TrackerStatus[]> StatusesAsync(TrackerCredential credential, string scope, CancellationToken ct);
    /// <summary>The boards of the scope: Jira agile boards of the project, Azure DevOps teams.</summary>
    Task<TrackerBoard[]> BoardsAsync(TrackerCredential credential, string scope, CancellationToken ct);
    /// <summary>What the intake query would pick up now — on <paramref name="board"/> only, when set.</summary>
    Task<TrackerPreview> PreviewAsync(TrackerCredential credential, string scope, string[] epicKeys,
        string? extraClause, bool includeDescendants, string[]? intakeStatuses, string? scopeMode, CancellationToken ct,
        TrackerBoardRef? board = null);
}

/// <summary>Shared HTTP plumbing for the tracker clients.</summary>
internal static class TrackerHttp
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The runner's own client: no redirects (Azure DevOps answers a bad PAT with a
    /// redirect to its sign-in page, which must read as "bad token", not as HTML to parse),
    /// and a deadline well inside the host's op budget.</summary>
    public static HttpClient CreateDefault() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

    public static HttpRequestMessage Request(HttpMethod method, string url, TrackerCredential credential, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential.BasicValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("orkka");
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        return request;
    }

    public static async Task<(HttpStatusCode Status, string Body, string? MediaType)> SendAsync(
        HttpClient http, HttpRequestMessage request, string trackerName, CancellationToken ct)
    {
        try
        {
            using (request)
            using (var response = await http.SendAsync(request, ct))
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return (response.StatusCode, body, response.Content.Headers.ContentType?.MediaType);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new TrackerException($"Could not reach {trackerName} at {request.RequestUri?.GetLeftPart(UriPartial.Authority)}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TrackerException($"{trackerName} did not answer in time ({request.RequestUri?.GetLeftPart(UriPartial.Authority)}).");
        }
    }

    public static JsonElement Parse(string body, string trackerName)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new TrackerException($"{trackerName} answered with something that is not JSON.");
        }
    }

    public static string? Str(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static JsonElement Prop(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static IEnumerable<JsonElement> Items(this JsonElement e)
        => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
}
