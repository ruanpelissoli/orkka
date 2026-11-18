namespace Orkka.Local.Auth;

using System.Security.Claims;
using Orkka.Modules.Organizations.Contracts;
using Orkka.SharedKernel.Caching;

/// <summary>
/// Resolves the caller's membership into the <see cref="CurrentUserKeys"/> items that
/// <see cref="CurrentUser"/> reads. A trimmed copy of the old cloud host's middleware: the
/// single local user (<see cref="LocalAuthHandler.LocalUserId"/>) is provisioned as Owner of a
/// default workspace on first use; invites, project-scoped tokens, platform admins,
/// suspension and rate limiting are gone.
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next)
{
    /// <summary>The org id of the workspace (job) the client is showing.</summary>
    public const string WorkspaceHeader = "X-Orkka-Workspace";

    public async Task InvokeAsync(
        HttpContext context,
        IOrgMembershipService membership,
        IOrkkaCache cache)
    {
        var locale = AgentLanguage.Normalize(context.Request.Headers.AcceptLanguage.FirstOrDefault());
        if (locale is not null)
            context.Items[CurrentUserKeys.Locale] = locale;

        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Org API keys: OrgApiKeyAuthHandler already populated the full CurrentUser item
            // set (creator identity, Member role, permission subset) — never Owner.
            if (context.User.HasClaim(Orkka.Modules.Organizations.Features.ApiKeys.OrgApiKeyAuthHandler.ClaimType, "true"))
            {
                await next(context);
                return;
            }

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.User.FindFirstValue("sub");

            if (userId is not null)
            {
                context.Items[CurrentUserKeys.UserId] = userId;

                // One org per job: the client names the workspace it is showing. Naming one the
                // caller does not belong to is refused outright rather than quietly answered
                // from another org.
                if (context.Request.Headers.TryGetValue(WorkspaceHeader, out var workspaceHeader)
                    && !string.IsNullOrWhiteSpace(workspaceHeader))
                {
                    if (!Ulid.TryParse(workspaceHeader.ToString(), out var requested))
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await context.Response.WriteAsJsonAsync(new { message = "Invalid workspace." }, context.RequestAborted);
                        return;
                    }

                    var inWorkspace = await cache.GetOrCreateAsync(
                        CacheKeys.Membership(userId, requested),
                        async ct => await membership.ResolveInOrgAsync(userId, requested, ct),
                        CachePolicies.Auth,
                        context.RequestAborted);
                    if (inWorkspace is null)
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(
                            new { message = "You are not a member of that workspace." }, context.RequestAborted);
                        return;
                    }

                    ApplyMember(context, inWorkspace);
                    await next(context);
                    return;
                }

                // No workspace named: the active one, provisioning the default workspace on the
                // very first request.
                var member = await cache.GetOrCreateAsync(
                    CacheKeys.Membership(userId),
                    async ct => await membership.ResolveAsync(userId, ct)
                        ?? await membership.ResolveOrProvisionAsync(userId, ct),
                    CachePolicies.Auth,
                    context.RequestAborted);

                ApplyMember(context, member);
            }
        }

        await next(context);
    }

    private static void ApplyMember(HttpContext context, MembershipInfo? member)
    {
        if (member is null)
            return;

        context.Items[CurrentUserKeys.OrgId] = member.OrgId;
        context.Items[CurrentUserKeys.MemberId] = member.MemberId;
        context.Items[CurrentUserKeys.Role] = member.Role;
        context.Items[CurrentUserKeys.Permissions] =
            (IReadOnlySet<Permission>)member.Permissions.ToHashSet();

        // The saved interface language outranks the Accept-Language read above: the header is
        // whatever client sent the request, this is what the user actually picked.
        if (member.Locale is not null)
            context.Items[CurrentUserKeys.Locale] = member.Locale;
    }
}

public static class CurrentUserMiddlewareExtensions
{
    public static IApplicationBuilder UseCurrentUser(this IApplicationBuilder builder)
        => builder.UseMiddleware<CurrentUserMiddleware>();
}
