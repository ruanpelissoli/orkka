namespace Orkka.Local.Auth;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Contracts;
using Orkka.Modules.Runners.Domain;
using Orkka.Modules.Runners.Infrastructure;
using Orkka.Modules.Runners.Infrastructure.Services;

public sealed record LaunchCodeResponse(string Code);

/// <param name="MachineName">Names the runner token minted for this machine; defaults to the
/// host's machine name.</param>
public sealed record BootstrapRequest(string? MachineName);

/// <param name="Token">The raw <c>orkr_</c> runner token, present only when one was minted by
/// THIS call (the workspace had no live token for this machine). Never retrievable again.</param>
public sealed record BootstrapWorkspace(string WorkspaceId, string Name, string? Token);

/// <summary>
/// The launcher's endpoints. Every <c>/local/*</c> call needs the install secret as a bearer —
/// a browser session is deliberately not enough — and <c>/launch</c> is the browser's way in.
/// </summary>
public static class LocalEndpoints
{
    public static void MapLocalEndpoints(this WebApplication app)
    {
        var local = app.MapGroup("/local").AllowAnonymous();

        // A one-time code the launcher puts in the URL it opens: GET /launch?code=…
        local.MapPost("/launch-codes", (HttpContext http, LocalSettings settings, LaunchCodeStore codes) =>
            IsInstallSecret(http, settings)
                ? Results.Ok(new LaunchCodeResponse(codes.Issue()))
                : Results.Unauthorized());

        local.MapPost("/bootstrap", BootstrapAsync);

        local.MapPost("/shutdown", (HttpContext http, LocalSettings settings,
            IHostApplicationLifetime lifetime, ILogger<LocalSettings> logger) =>
        {
            if (!IsInstallSecret(http, settings))
                return Results.Unauthorized();

            logger.LogInformation("Shutdown requested by the launcher");
            // After the response is written, so the caller sees its 202.
            http.Response.OnCompleted(() =>
            {
                lifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.Accepted();
        });

        app.MapGet("/launch", (HttpContext http, LaunchCodeStore codes, LocalSessionStore sessions) =>
        {
            if (!codes.TryConsume(http.Request.Query["code"].ToString()))
                return Results.Content(InvalidLaunchPage, "text/html; charset=utf-8", statusCode: StatusCodes.Status403Forbidden);

            LocalAuthHandler.AppendSessionCookie(http.Response, sessions.Create());
            http.Response.Headers.CacheControl = "no-store";
            return Results.Redirect("/");
        }).AllowAnonymous();
    }

    /// <summary>
    /// Makes sure the local user and a default workspace exist, then mints an <c>orkr_</c> runner
    /// token for every workspace that has no live token named for this machine — what the
    /// launcher writes into the runner's per-workspace profiles.
    /// </summary>
    private static async Task<IResult> BootstrapAsync(
        HttpContext http,
        LocalSettings settings,
        IOrgMembershipService membership,
        RunnersDbContext db,
        Orkka.Modules.Agents.Contracts.IAgentAuditLog auditLog,
        CancellationToken ct)
    {
        if (!IsInstallSecret(http, settings))
            return Results.Unauthorized();

        BootstrapRequest? request = null;
        if (http.Request.ContentLength is > 0 || http.Request.ContentType is not null)
        {
            try { request = await http.Request.ReadFromJsonAsync<BootstrapRequest>(ct); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { message = "Invalid JSON." }); }
        }

        var machineName = string.IsNullOrWhiteSpace(request?.MachineName)
            ? Environment.MachineName
            : request.MachineName.Trim();
        if (machineName.Length > 100)
            return Results.BadRequest(new { message = "Machine name is longer than 100 characters." });

        const string userId = LocalAuthHandler.LocalUserId;
        await membership.ResolveOrProvisionAsync(userId, ct);
        var workspaces = await membership.ListWorkspacesAsync(userId, ct);

        var now = DateTimeOffset.UtcNow;
        var result = new List<BootstrapWorkspace>(workspaces.Count);
        foreach (var workspace in workspaces)
        {
            // Across workspaces: the request's own tenant filter only covers the active one.
            var hasLiveToken = await db.RunnerTokens.IgnoreQueryFilters().AnyAsync(t =>
                t.OrgId == workspace.OrgId && t.UserId == userId && t.Name == machineName
                && t.RevokedAt == null && t.ExpiresAt > now, ct);
            if (hasLiveToken)
            {
                result.Add(new BootstrapWorkspace(workspace.OrgId.ToString(), workspace.Name, null));
                continue;
            }

            var raw = RunnerTokenValues.Generate();
            var token = new RunnerToken
            {
                OrgId = workspace.OrgId,
                UserId = userId,
                Name = machineName,
                TokenHash = RunnerTokenValues.Hash(raw),
                ExpiresAt = now + RunnerTokenValues.Lifetime,
            };
            db.RunnerTokens.Add(token);
            await db.SaveChangesAsync(ct);

            await auditLog.RecordAsync(new Orkka.Modules.Agents.Contracts.AgentAuditRecord(
                workspace.OrgId, null,
                Orkka.Modules.Agents.Contracts.AgentAuditEventType.RunnerTokenCreated,
                $"Runner token \"{machineName}\" created by the launcher",
                Actor: userId,
                ActorKind: Orkka.Modules.Agents.Contracts.AuditActorKind.User,
                ActorUserId: userId,
                Metadata: new { tokenId = token.Id.ToString(), token.Name }), ct);

            result.Add(new BootstrapWorkspace(workspace.OrgId.ToString(), workspace.Name, raw));
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(result);
    }

    private static bool IsInstallSecret(HttpContext http, LocalSettings settings)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && settings.IsInstallSecret(header["Bearer ".Length..].Trim());
    }

    private static readonly string InvalidLaunchPage = $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>Orkka</title>
        <style>body{font-family:system-ui,sans-serif;max-width:32rem;margin:15vh auto;padding:0 1rem;color:#222}</style>
        </head><body>
        <h1>This link has expired</h1>
        <p>Launch links work once, for {{(int)LaunchCodeStore.Lifetime.TotalSeconds}} seconds.
        Run <code>./orkka open</code> in your Orkka folder to get a new one.</p>
        </body></html>
        """;
}
