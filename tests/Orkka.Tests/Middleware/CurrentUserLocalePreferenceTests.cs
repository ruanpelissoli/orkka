namespace Orkka.Tests.Middleware;

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Orkka.Modules.Organizations.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Tests.Caching;
using HostedMiddleware = Orkka.Local.Auth.CurrentUserMiddleware;
using HostedKeys = Orkka.Local.Auth.CurrentUserKeys;

/// <summary>
/// CurrentUserLocaleTests covers the header hop. This covers the one that outranks it: the
/// language the member SAVED. Accept-Language is only ever as good as the client that sent
/// it — a request routed through a proxy, a native client, or a page that lost its locale
/// prefix all report English, and the agent then answers a Portuguese user in English for a
/// whole conversation. The stored preference is the durable answer, so it wins whenever it
/// exists and stays out of the way when it does not.
/// </summary>
public sealed class CurrentUserLocalePreferenceTests : IDisposable
{
    private const string UserId = "user_locale_test";

    private readonly TestCache _cache = new();

    public void Dispose() => _cache.Dispose();

    private static DefaultHttpContext SignedInContext(string? acceptLanguage)
    {
        var context = new DefaultHttpContext();
        if (acceptLanguage is not null)
            context.Request.Headers.AcceptLanguage = acceptLanguage;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId)], authenticationType: "Test"));
        return context;
    }

    private static MembershipInfo Membership(string? locale) => new(
        Ulid.NewUlid(), Ulid.NewUlid(), OrgRole.Owner, [], Locale: locale);

    private async Task<object?> RunHostedAsync(string? acceptLanguage, string? savedLocale)
    {
        var context = SignedInContext(acceptLanguage);

        await new HostedMiddleware(next: _ => Task.CompletedTask).InvokeAsync(
            context,
            new StubMembership(Membership(savedLocale)),
            _cache.Cache);

        context.Items.TryGetValue(HostedKeys.Locale, out var locale);
        return locale;
    }

    [Fact]
    public async Task SavedLanguage_BeatsWhateverTheClientSentAsAcceptLanguage()
        => (await RunHostedAsync(acceptLanguage: "en-US,en;q=0.9", savedLocale: "pt"))
            .Should().Be("pt");

    [Fact]
    public async Task SavedLanguage_AppliesWhenTheRequestCarriesNoHeaderAtAll()
        => (await RunHostedAsync(acceptLanguage: null, savedLocale: "es")).Should().Be("es");

    [Fact]
    public async Task Header_StillDecidesForAMemberWhoNeverPickedALanguage()
        => (await RunHostedAsync(acceptLanguage: "pt-BR", savedLocale: null)).Should().Be("pt");

    private sealed class StubMembership(MembershipInfo member) : IOrgMembershipService
    {
        public Task<MembershipInfo?> ResolveAsync(string userId, CancellationToken ct = default)
            => Task.FromResult<MembershipInfo?>(member);

        public Task<MembershipInfo> ResolveOrProvisionAsync(string userId, CancellationToken ct = default)
            => Task.FromResult(member);

        public Task<Ulid?> GetOrgIdForUserAsync(string userId, CancellationToken ct = default)
            => Task.FromResult<Ulid?>(member.OrgId);

        public Task<MembershipInfo?> ResolveInOrgAsync(string userId, Ulid orgId, CancellationToken ct = default)
            => Task.FromResult<MembershipInfo?>(orgId == member.OrgId ? member : null);

        public Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(string userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkspaceInfo>>(
                [new WorkspaceInfo(member.OrgId, "Workspace", "workspace", member.Role, true)]);

        public Task<WorkspaceInfo> CreateWorkspaceAsync(string userId, string name, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SetActiveWorkspaceAsync(string userId, Ulid orgId, CancellationToken ct = default)
            => Task.FromResult(orgId == member.OrgId);
    }
}
