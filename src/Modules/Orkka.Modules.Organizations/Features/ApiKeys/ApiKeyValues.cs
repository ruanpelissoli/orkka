namespace Orkka.Modules.Organizations.Features.ApiKeys;

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Org API keys are <c>orkka_key_</c> + 32 random bytes base64url-encoded. Only the SHA-256
/// hex hash is persisted; the raw key is the capability and is shown exactly once.
/// </summary>
public static class ApiKeyValues
{
    public const string Prefix = "orkka_key_";
    public const int DisplayPrefixLength = 16;

    public static string Generate() =>
        Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string DisplayPrefix(string key) =>
        key.Length <= DisplayPrefixLength ? key : key[..DisplayPrefixLength];
}
