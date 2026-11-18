namespace Orkka.Modules.Tasks.Features;

using FastEndpoints;
using Orkka.Modules.Tasks.Contracts;

/// <summary>Maps a failed <see cref="BoardOpOutcome"/> to the HTTP response the endpoints
/// always returned: NotFound → 404; Conflict/Invalid → 400 with the message.</summary>
internal static class BoardOpEndpointExtensions
{
    public static async Task SendBoardFailureAsync(this IEndpoint endpoint, BoardOpOutcome outcome, CancellationToken ct)
    {
        if (outcome.Failure == BoardOpFailure.NotFound)
        {
            endpoint.HttpContext.Response.StatusCode = 404;
            await endpoint.HttpContext.Response.StartAsync(ct);
            return;
        }
        endpoint.ValidationFailures.Add(new FluentValidation.Results.ValidationFailure("GeneralErrors", outcome.Message ?? "Request failed."));
        await endpoint.HttpContext.Response.SendErrorsAsync(endpoint.ValidationFailures, 400, cancellation: ct);
    }
}
