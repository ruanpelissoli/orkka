namespace Orkka.Modules.Agents.Features.Models;

using Orkka.Modules.Agents.Contracts;
using FastEndpoints;

/// <param name="IsEnabled">False = retired. Returned anyway so the UI can render a real label
/// for an agent already pinned to it instead of a raw id, and so it can be marked unselectable
/// rather than silently replaced.</param>
/// <param name="RequiresRunner">Only runs through a vendor CLI on a connected runner — the
/// hosted UI hides these.</param>
/// <param name="Kind">Always "Chat": legacy image-model rows (from when Orkka generated images
/// server-side) are not listed.</param>
public sealed record AiModelResponse(
    string Id,
    string Name,
    string Provider,
    string Kind,
    string Description,
    bool SupportsMcp,
    bool IsEnabled,
    bool RequiresRunner,
    int SortOrder);

/// <summary>
/// The model catalog the frontends render. Deliberately does NOT expose rates: this endpoint is
/// publicly reachable, and the per-1M rates are Orkka's vendor cost basis (the customer-facing
/// markup is already published) — see the admin endpoints for those.
/// </summary>
public sealed class ListModelsEndpoint(IModelCatalog catalog)
    : EndpointWithoutRequest<List<AiModelResponse>>
{
    public override void Configure()
    {
        Get("/api/models");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var snapshot = await catalog.GetAsync(ct);

        // Grouped by provider in first-appearance order, then by SortOrder within the group.
        // Sorting by SortOrder alone would interleave providers and shatter the <optgroup>s the
        // pickers build from this list.
        var models = snapshot.All
            .Where(m => m.Kind == AiModelKind.Chat)
            .OrderBy(m => m.SortOrder)
            .GroupBy(m => m.Provider)
            .SelectMany(g => g)
            .Select(m => new AiModelResponse(
                m.ModelId, m.DisplayName, m.Provider.ToString(), m.Kind.ToString(),
                m.Description, m.SupportsMcp, m.IsEnabled, m.RequiresRunner, m.SortOrder))
            .ToList();

        await Send.OkAsync(models, ct);
    }
}
