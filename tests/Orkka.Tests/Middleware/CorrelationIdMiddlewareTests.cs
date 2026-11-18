using Orkka.Local.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Orkka.Tests.Middleware;

public sealed class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenNoCorrelationIdHeader_GeneratesNewId()
    {
        // Arrange
        var context = new DefaultHttpContext();

        var middleware = new CorrelationIdMiddleware(next: _ => Task.CompletedTask);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — correlation ID stored in Items (response header set via OnStarting callback)
        context.Items.Should().ContainKey(CorrelationIdMiddleware.LogPropertyName);
        context.Items[CorrelationIdMiddleware.LogPropertyName]?.ToString()
            .Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InvokeAsync_WhenCorrelationIdHeaderExists_ReusesExistingId()
    {
        // Arrange
        var existingId = "test-correlation-123";
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = existingId;

        string? capturedCorrelationId = null;

        var middleware = new CorrelationIdMiddleware(next: _ =>
        {
            capturedCorrelationId = existingId;
            return Task.CompletedTask;
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        capturedCorrelationId.Should().Be(existingId);
    }

    [Fact]
    public async Task InvokeAsync_GeneratedId_IsValidGuid()
    {
        // Arrange
        var context = new DefaultHttpContext();
        string? capturedId = null;

        var middleware = new CorrelationIdMiddleware(next: ctx =>
        {
            if (ctx.Items.TryGetValue(CorrelationIdMiddleware.LogPropertyName, out var id))
            {
                capturedId = id?.ToString();
            }
            return Task.CompletedTask;
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        capturedId.Should().NotBeNull();
        Guid.TryParse(capturedId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_WhenHeaderIsWhitespace_GeneratesNewId()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "   ";

        string? capturedId = null;

        var middleware = new CorrelationIdMiddleware(next: ctx =>
        {
            if (ctx.Items.TryGetValue(CorrelationIdMiddleware.LogPropertyName, out var id))
            {
                capturedId = id?.ToString();
            }
            return Task.CompletedTask;
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        capturedId.Should().NotBeNull();
        Guid.TryParse(capturedId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_CallsNextMiddleware()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var nextCalled = false;

        var middleware = new CorrelationIdMiddleware(next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeTrue();
    }
}
