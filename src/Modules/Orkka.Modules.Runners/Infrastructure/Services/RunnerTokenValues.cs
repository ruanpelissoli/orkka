namespace Orkka.Modules.Runners.Infrastructure.Services;

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Runner tokens are <c>orkr_</c> + 32 random bytes base64url-encoded. Only the SHA-256
/// hex hash is persisted; the raw token is the capability and is shown exactly once.
/// </summary>
public static class RunnerTokenValues
{
    public const string Prefix = "orkr_";

    /// <summary>How long a new token works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    /// <summary>A connected runner whose token expires within this window is given a new one.</summary>
    public static readonly TimeSpan RotateWithin = TimeSpan.FromDays(30);

    /// <summary>How long a rotated-out token keeps working — enough for the runner to store the
    /// new one and redial, short enough that a stolen copy is soon worthless.</summary>
    public static readonly TimeSpan RotationGrace = TimeSpan.FromHours(24);

    public static string Generate() =>
        Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
