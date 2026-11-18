namespace Orkka.SharedKernel.Security;

using FastEndpoints;

public static class PermissionGuard
{
    /// <summary>
    /// True when the caller may proceed; otherwise sends a 403 and returns false.
    /// The response is STARTED here — merely assigning Response.StatusCode is not enough,
    /// because FastEndpoints auto-sends a success response when a handler completes
    /// without sending one, silently overwriting the status.
    /// </summary>
    public static async Task<bool> AuthorizeAsync(
        ICurrentUser currentUser, IEndpoint endpoint, Permission permission)
    {
        if (currentUser.HasPermission(permission))
            return true;

        endpoint.HttpContext.Response.StatusCode = 403;
        await endpoint.HttpContext.Response.StartAsync(endpoint.HttpContext.RequestAborted);
        return false;
    }
}
