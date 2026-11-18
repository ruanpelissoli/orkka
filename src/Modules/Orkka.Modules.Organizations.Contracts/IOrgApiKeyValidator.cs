namespace Orkka.Modules.Organizations.Contracts;

using Orkka.SharedKernel.Security;

/// <summary>
/// What an org API key resolves to: the member who created it, acting with the key's
/// permission subset (further intersected with the member's live permissions, so a demoted
/// member's key shrinks with them). Never Owner — a key is a scoped delegate, not the person.
/// </summary>
public sealed record ApiKeyPrincipal(
    Ulid KeyId,
    string Name,
    Ulid OrgId,
    Ulid MemberId,
    string UserId,
    IReadOnlyList<Permission> Permissions,
    /// <summary>When set, the key may only reach this project.</summary>
    Ulid? ProjectId);

/// <summary>Validates raw <c>orkka_key_…</c> bearer tokens for the auth handler. Cross-org by
/// design: runs during authentication, before any tenant context exists.</summary>
public interface IOrgApiKeyValidator
{
    Task<ApiKeyPrincipal?> ValidateAsync(string rawKey, CancellationToken ct = default);
}
