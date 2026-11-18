namespace Orkka.Modules.Runners.PublicApi;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Runners.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>
/// The API host's <see cref="IUserRunnerUtility"/>: serves a one-shot utility completion
/// (a brief interview turn, brief/PRD synthesis, a task plan) on the user's own runner by
/// relaying to the Worker pod that holds their socket — their subscription, $0.
///
/// Exists because the Worker's <c>RunnerLinkUtility</c> only covers utility calls made ON
/// the Worker; a request handler in the API host (run-config detection, …) has no runner
/// socket of its own.
///
/// Routes whenever the user's runner is online, whatever their execution preference: the
/// runner is the only place utility work runs (there is no platform-key fallback), so
/// declining a connected runner would only turn a working call into a failed one. Every miss
/// (runner offline, relay unconfigured, relay call failed) returns null — the caller takes
/// its runner-offline path — never throws.
/// </summary>
public sealed class RelayUserRunnerUtility(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<RelayUserRunnerUtility> logger) : IUserRunnerUtility
{
    /// <summary>Own named client: the runner gives utility generations a 15-minute op
    /// ceiling (thinking models on a long synthesis), so the hop must outlive that — the
    /// 30-second ops client would abandon a perfectly healthy brief synthesis.</summary>
    public const string HttpClientName = "runner-relay-utility";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AnthropicResponse?> TryCompleteAsync(
        Ulid orgId, string? userId, AnthropicRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId))
            return null;

        try
        {
            RunnerRelayTarget? relay;
            using (var scope = scopeFactory.CreateScope())
            {
                relay = await scope.ServiceProvider
                    .GetRequiredService<IRunnerRelayLocator>()
                    .LocateAsync(orgId, userId, ct);
            }

            if (relay is null)
                return null;

            // The runner's UtilityComplete op forwards system + user text only, exactly as
            // RunnerAnthropicClient does in-process: assistant turns and image blocks would
            // be dropped there anyway, so callers already flatten (see BriefChatService).
            var userMessage = string.Join("\n\n", request.Messages
                .Where(m => m.Role == "user")
                .SelectMany(m => m.Content)
                .OfType<AnthropicTextContent>()
                .Select(c => c.Text));

            var payload = new RelayUtilityRequest(
                orgId.ToString(), userId, request.Model, request.System, userMessage, request.MaxTokens);

            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(
                $"{relay.BaseUrl}/internal/runner/utility", payload, Json, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<RelayUtilityResponse>(Json, ct);
            if (result is not { Served: true })
                return null;

            return new AnthropicResponse
            {
                Text = result.Text ?? "",
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
                CacheReadTokens = result.CacheReadTokens,
                CacheWriteTokens = result.CacheWriteTokens,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes the client timeout (a TaskCanceledException with ct still live):
            // a wedged relay is a miss, and the caller takes its runner-offline path.
            logger.LogInformation(ex,
                "Could not relay a utility call to {UserId}'s runner — the feature takes its runner-offline path",
                userId);
            return null;
        }
    }
}

// Wire shapes shared with the Worker's /internal/runner/utility endpoint (duplicated there
// by design — the Worker cannot reference this module's internals; the contract is two
// flat records with web-cased JSON, deployed together).
public sealed record RelayUtilityRequest(
    string OrgId, string UserId, string Model, string? System, string UserMessage, int MaxTokens);

public sealed record RelayUtilityResponse(
    bool Served, string? Text = null,
    int InputTokens = 0, int OutputTokens = 0, int CacheReadTokens = 0, int CacheWriteTokens = 0);
