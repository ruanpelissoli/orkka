namespace Orkka.Runners.Server;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>
/// The hybrid Worker's <see cref="IUserRunnerUtility"/>: serves a utility completion on the
/// user's connected runner over the same <c>UtilityComplete</c> op Orkka Local uses — their
/// own subscription, $0 — the only place customer utility calls run (there is no platform-key
/// fallback).
///
/// Provider-transparent: delegates to <see cref="RunnerAnthropicClient"/>, which runs on
/// Claude when the runner has it and otherwise tier-maps onto the first connected provider
/// (codex, gemini, copilot…) — a GPT-only or Copilot-only machine serves these calls just
/// like a Claude one. A runner with NO authenticated provider is a miss.
///
/// Every miss returns null, never throws — the caller takes its runner-offline path; a
/// dependency analysis must not crash because a laptop lid closed mid-call.
/// </summary>
public sealed class RunnerLinkUtility(
    IRunnerLinkProvider links,
    IServiceScopeFactory scopeFactory,
    ILogger<RunnerLinkUtility> logger) : IUserRunnerUtility
{
    public async Task<AnthropicResponse?> TryCompleteAsync(
        Ulid orgId, string? userId, AnthropicRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId))
            return null;

        try
        {
            var link = links.GetLink(orgId, userId);
            var readiness = new LinkProviderReadiness(link);
            if (!link.IsConnected || readiness.GetAnyProviderBlocker() is not null)
                return null;

            // The catalog maps a requested claude model onto the serving provider's
            // tier-equivalent. Scoped service, and this class is a singleton — one scope
            // per call is cheap next to a model generation.
            await using var scope = scopeFactory.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetService<IModelCatalog>();

            var client = new RunnerAnthropicClient(link, readiness, catalog);
            return await client.CreateMessageAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex,
                "Could not serve a utility call on {UserId}'s runner — the feature takes its runner-offline path",
                userId);
            return null;
        }
    }
}

/// <summary>Per-link provider readiness: mirrors the runner's last Hello (which vendor CLIs
/// are signed in on that machine). The link-scoped twin of the Worker's route readiness,
/// living here so utility routing needs no host-assembly types.</summary>
internal sealed class LinkProviderReadiness(IRunnerLink link) : IProviderReadiness
{
    private const string RunnerOffline = "The runner is not connected.";

    private static readonly AiProvider[] PreferenceOrder = ProviderPreference.Order;

    public string? GetBlocker(AiProvider provider)
    {
        var (connected, _) = link.Status;
        return connected ? link.GetProviderBlocker(provider) : RunnerOffline;
    }

    public IReadOnlyList<AiProvider> Connected
    {
        get
        {
            var (connected, hello) = link.Status;
            if (!connected || hello is null)
                return [];
            return PreferenceOrder
                .Where(p => hello.Providers.Any(s =>
                    s.Provider == RunnerProviders.FromAiProviderName(p.ToString()) && s.Authenticated))
                .ToList();
        }
    }

    public string? GetAnyProviderBlocker()
    {
        var (connected, hello) = link.Status;
        if (!connected || hello is null)
            return RunnerOffline;
        return hello.Providers.Any(p => p.Authenticated)
            ? null
            : "No AI provider is signed in on the runner.";
    }
}
