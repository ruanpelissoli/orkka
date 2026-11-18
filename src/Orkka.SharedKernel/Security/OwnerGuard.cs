namespace Orkka.SharedKernel.Security;

using FastEndpoints;

public static class OwnerGuard
{
    /// <summary>
    /// True when the caller is an Owner; otherwise sends a 403 and returns false.
    /// See <see cref="PermissionGuard"/> for why the response must be started here.
    /// </summary>
    public static async Task<bool> AuthorizeOwnerAsync(ICurrentUser currentUser, IEndpoint endpoint)
    {
        if (currentUser.IsOwner)
            return true;

        endpoint.HttpContext.Response.StatusCode = 403;
        await endpoint.HttpContext.Response.StartAsync(endpoint.HttpContext.RequestAborted);
        return false;
    }
}
