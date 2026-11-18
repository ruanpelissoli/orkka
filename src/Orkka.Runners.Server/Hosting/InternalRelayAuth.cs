namespace Orkka.Runners.Server.Hosting;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The shared check behind every <c>/internal/*</c> relay endpoint: the caller presents the
/// host's per-process <c>WorkerAuth:ApiKey</c> as <c>X-Worker-Key</c>. In Orkka.Local the key
/// is generated at startup and held in memory only, and the request guard additionally
/// refuses any <c>/internal</c> request that does not come from a loopback address.
/// </summary>
public static class InternalRelayAuth
{
    public const string HeaderName = "X-Worker-Key";

    public static bool IsAuthorized(HttpContext http, IConfiguration configuration)
        => KeyMatches(configuration["WorkerAuth:ApiKey"], http.Request.Headers[HeaderName].FirstOrDefault());

    /// <summary>Constant-time comparison; an unconfigured key matches nothing.</summary>
    public static bool KeyMatches(string? expected, string? presented)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
    }
}
