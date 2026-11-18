namespace Orkka.SharedKernel.Security;

/// <summary>A user's display identity. Nothing about it is stored in Orkka's tables.</summary>
public sealed record UserInfo(string UserId, string? Email, string? Name);

/// <summary>
/// Display identity for user ids (actor names on revisions, design assets, diagrams). The host
/// supplies the implementation: Orkka.Local answers for its single local user.
/// </summary>
public interface IUserDirectory
{
    Task<UserInfo> GetUserAsync(string userId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, UserInfo>> GetUsersAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct = default);
}
