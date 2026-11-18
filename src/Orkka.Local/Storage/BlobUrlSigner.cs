namespace Orkka.Local.Storage;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// HMAC signatures for <c>/blobs/…</c> URLs — the local stand-in for Azure SAS tokens. A URL
/// carries <c>exp</c> (unix seconds) and <c>sig</c> = HMAC-SHA256 over the method, the container,
/// the blob name and the expiry. The key is derived from the install's encryption key, so URLs
/// handed out before a restart keep working until they expire.
/// </summary>
public sealed class BlobUrlSigner
{
    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    public BlobUrlSigner(IConfiguration configuration, TimeProvider? clock = null)
        : this(DeriveKey(configuration["Encryption:Key"]
            ?? throw new InvalidOperationException("Encryption:Key is not configured.")), clock)
    {
    }

    public BlobUrlSigner(byte[] key, TimeProvider? clock = null)
    {
        _key = key;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The query string (no leading '?') authorizing <paramref name="method"/> on the blob.</summary>
    public string Sign(string method, string container, string blob, TimeSpan expiresIn)
    {
        var exp = (_clock.GetUtcNow() + expiresIn).ToUnixTimeSeconds();
        return $"exp={exp.ToString(CultureInfo.InvariantCulture)}&sig={Compute(method, container, blob, exp)}";
    }

    public bool Verify(string method, string container, string blob, string? exp, string? sig)
    {
        if (string.IsNullOrEmpty(sig) || sig.Length != 64
            || !long.TryParse(exp, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAt)
            || expiresAt <= _clock.GetUtcNow().ToUnixTimeSeconds())
            return false;

        var expected = Compute(method, container, blob, expiresAt);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig.ToLowerInvariant()));
    }

    private string Compute(string method, string container, string blob, long exp)
    {
        var payload = $"{method.ToUpperInvariant()}\n{container}\n{blob}\n{exp.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));
    }

    private static byte[] DeriveKey(string encryptionKeyBase64) =>
        HMACSHA256.HashData(Convert.FromBase64String(encryptionKeyBase64),
            Encoding.UTF8.GetBytes("orkka-local/blob-urls/v1"));
}
