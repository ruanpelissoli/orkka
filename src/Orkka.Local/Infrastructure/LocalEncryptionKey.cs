namespace Orkka.Local.Infrastructure;

/// <summary>
/// The AES key protecting secrets at rest (<c>Encryption:Key</c>, base64 of 32 bytes).
/// <c>./orkka start</c> keeps it in the OS secret store (generated on first start) and hands it
/// to the host in memory — never through the environment, never in a file Orkka writes. A host
/// built without one refuses to start rather than silently minting a key nothing remembers.
/// </summary>
public static class LocalEncryptionKey
{
    public const string ConfigKey = "Encryption:Key";

    public static void Require(IConfiguration configuration)
    {
        var value = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Encryption:Key is not configured. Start Orkka with `./orkka start`, which keeps it " +
                "in your OS secret store and hands it to the host in memory.");
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException) { bytes = []; }
        if (bytes.Length != 32)
            throw new InvalidOperationException("Encryption:Key must be the base64 encoding of 32 bytes.");
    }
}
