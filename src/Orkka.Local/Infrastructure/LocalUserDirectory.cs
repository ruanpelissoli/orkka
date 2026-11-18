namespace Orkka.Local.Infrastructure;

/// <summary>
/// The single local user's display identity, with no network call: the OS account name.
/// Any other id resolves to no name.
/// </summary>
public sealed class LocalUserDirectory : IUserDirectory
{
    public Task<UserInfo> GetUserAsync(string userId, CancellationToken ct = default)
        => Task.FromResult(Resolve(userId));

    public Task<IReadOnlyDictionary<string, UserInfo>> GetUsersAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, UserInfo>>(
            userIds.Distinct(StringComparer.Ordinal)
                .ToDictionary(id => id, Resolve, StringComparer.Ordinal));

    private static UserInfo Resolve(string userId) =>
        userId == Auth.LocalAuthHandler.LocalUserId
            ? new UserInfo(userId, null, Environment.UserName)
            : new UserInfo(userId, null, null);
}
