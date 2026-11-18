namespace Orkka.Modules.Agents.Features.Mcp;

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>
/// Hosts <see cref="OrkkaMcpToolServer"/> over MCP Streamable HTTP at <c>/mcp</c>. Stateless
/// (each POST is one authenticated request — no server-side session, which is what lets the
/// endpoint sit behind the same auth as the REST API and scale like it). The tool list and
/// every call are computed per request from the caller's identity, so two keys with different
/// permissions see different tools.
/// </summary>
public static class McpHosting
{
    public static IServiceCollection AddOrkkaMcp(this IServiceCollection services)
    {
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "orkka", Version = "1.0" };
                options.ServerInstructions =
                    "Orkka is an AI dev-team board: projects hold tasks that agents plan, code, test and " +
                    "review; humans approve plans, answer questions and merge. Start with list_projects, " +
                    "then use the project slug. get_inbox shows everything waiting on a human.";
                options.ScopeRequests = true;
                options.Handlers.ListToolsHandler = ListToolsAsync;
                options.Handlers.CallToolHandler = CallToolAsync;
            })
            .WithHttpTransport(transport => transport.Stateless = true);
        return services;
    }

    /// <summary>Maps <c>/mcp</c>; requires an authenticated caller (org API key or a signed-in
    /// session — whatever the host's default scheme resolves).</summary>
    public static IEndpointConventionBuilder MapOrkkaMcp(this IEndpointRouteBuilder app)
        => app.MapMcp("/mcp").RequireAuthorization();

    private static ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken ct)
    {
        var server = Resolve(context.Services);
        var tools = server.ListTools().Select(t => new Tool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = JsonDocument.Parse(t.InputSchemaJson).RootElement.Clone(),
        }).ToList();
        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    private static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var server = Resolve(context.Services);
        var name = context.Params?.Name ?? "";
        var args = context.Params?.Arguments is { } dict
            ? JsonSerializer.SerializeToElement(dict)
            : JsonDocument.Parse("{}").RootElement.Clone();
        var result = await server.CallToolAsync(name, args, ct);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = result.Text }],
            IsError = result.IsError,
        };
    }

    /// <summary>The caller comes from the request scope's ICurrentUser (populated by the API-key
    /// handler or the session middleware) plus the key's project restriction, if any.</summary>
    private static OrkkaMcpToolServer Resolve(IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var currentUser = services.GetRequiredService<ICurrentUser>();
        if (!currentUser.IsAuthenticated)
            throw new UnauthorizedAccessException("The MCP endpoint requires an authenticated caller.");

        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        Ulid? bound = null;
        var claim = http?.User.FindFirst("api_key_project")?.Value;
        if (claim is not null && Ulid.TryParse(claim, out var parsed)) bound = parsed;

        var apiKeyId = http?.User.FindFirst("api_key_id")?.Value;

        var caller = new McpCaller(
            currentUser.OrgId, currentUser.MemberId, currentUser.UserId, currentUser.Locale,
            currentUser.IsOwner, currentUser.Permissions, bound, apiKeyId);
        return new OrkkaMcpToolServer(
            caller,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ILogger<OrkkaMcpToolServer>>(),
            services.GetRequiredService<IAgentAuditLog>());
    }
}
