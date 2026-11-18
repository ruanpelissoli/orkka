namespace Orkka.Tests.Middleware;

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Orkka.SharedKernel.Utilities;
using HostedMiddleware = Orkka.Local.Auth.CurrentUserMiddleware;
using HostedKeys = Orkka.Local.Auth.CurrentUserKeys;

/// <summary>
/// The agent's reply language is decided by one header. Both hosts read Accept-Language
/// before any authentication branch, so an anonymous context is enough to pin the hop that
/// turns "the user picked Portuguese" into CurrentUser.Locale — the first place the whole
/// chain can fail silently, and the one that makes every reply come back in English.
/// </summary>
public sealed class CurrentUserLocaleTests
{
    private static DefaultHttpContext ContextWith(string? acceptLanguage)
    {
        var context = new DefaultHttpContext();
        if (acceptLanguage is not null)
            context.Request.Headers.AcceptLanguage = acceptLanguage;
        return context;
    }

    [Theory]
    [InlineData("pt", "pt")]
    [InlineData("pt-BR", "pt")]
    [InlineData("pt-BR,pt;q=0.9,en-US;q=0.8", "pt")]
    [InlineData("ES", "es")]
    public async Task HostedApi_ResolvesTheUiLocaleFromAcceptLanguage(string header, string expected)
    {
        var context = ContextWith(header);

        await new HostedMiddleware(next: _ => Task.CompletedTask)
            .InvokeAsync(context, null!, null!);

        context.Items[HostedKeys.Locale].Should().Be(expected);
    }

    /// <summary>English normalizes to "en" and is stored; an unsupported tag stores nothing.
    /// Both end up saying nothing in the prompt (AgentLanguage.DisplayName returns null for
    /// each), which is why this distinction is safe to leave as-is.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("ja", null)]
    [InlineData("en-US,en;q=0.9", "en")]
    public async Task HostedApi_StoresNothingThatWouldChangeTheReplyLanguage(string? header, string? expected)
    {
        var context = ContextWith(header);

        await new HostedMiddleware(next: _ => Task.CompletedTask)
            .InvokeAsync(context, null!, null!);

        context.Items.TryGetValue(HostedKeys.Locale, out var stored);
        (stored as string).Should().Be(expected);
        AgentLanguage.DisplayName(stored as string).Should().BeNull();
    }

}
