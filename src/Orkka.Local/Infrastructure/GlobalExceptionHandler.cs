using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Orkka.Local.Infrastructure;

/// <summary>
/// Turns unhandled exceptions into RFC 9457 ProblemDetails responses. Exception details
/// are included only in Development; other environments get a generic message plus the
/// correlation id so users can reference the failure in support/log lookups.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Client disconnected mid-request (common for SSE/streaming) — nothing to write to.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
            return true;

        logger.LogError(exception, "Unhandled exception for {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            Instance = $"{httpContext.Request.Method} {httpContext.Request.Path}",
        };

        if (GetCorrelationId(httpContext) is { } correlationId)
            problem.Extensions["correlationId"] = correlationId;

        if (environment.IsDevelopment())
            problem.Detail = exception.ToString();

        // False when the response already started (e.g. mid-SSE-stream) — the middleware
        // rethrows and the connection is torn down, which is all that can be done then.
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    // CorrelationIdMiddleware puts a generated id in Items but leaves client-supplied ids
    // in the request header only — check both.
    private static string? GetCorrelationId(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(CorrelationIdMiddleware.LogPropertyName, out var generated)
            && generated is string { Length: > 0 } id)
            return id;

        return httpContext.Request.Headers.TryGetValue(CorrelationIdMiddleware.HeaderName, out var supplied)
            && !string.IsNullOrWhiteSpace(supplied)
                ? supplied.ToString()
                : null;
    }
}
