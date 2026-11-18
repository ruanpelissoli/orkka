namespace Orkka.Modules.Agents.Infrastructure;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>
/// Provider readiness where no developer machine is in view — the API host and the Worker's
/// module defaults (agent seeding, task submission, the roster backfill). Every agent runs on the
/// developer's own runner with their own AI CLI subscription, and only that runner knows which
/// CLIs are signed in: its readiness (<c>HostedRunnerReadiness</c>, per route) is what blocks or
/// substitutes a provider at run time. So nothing here ever blocks, and the auto-pick default —
/// a seeded agent's model when nobody chose one — is Claude. Orkka's own vendor keys play no
/// part in it: they are not what runs a customer's work.
/// </summary>
public sealed class AlwaysReadyProviderReadiness : IProviderReadiness
{
    private static readonly IReadOnlyList<AiProvider> Default = [AiProvider.Anthropic];

    public string? GetBlocker(AiProvider provider) => null;

    public IReadOnlyList<AiProvider> Connected => Default;

    public string? GetAnyProviderBlocker() => null;
}
