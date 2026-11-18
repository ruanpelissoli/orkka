namespace Orkka.LocalRunner.Runtime;

using System.Security.Cryptography;
using Orkka.LocalRunner.Trackers;

/// <summary>The secrets the local stack runs with, generated on first start.</summary>
public sealed class LocalStackSecrets
{
    /// <summary>The bearer the CLI presents to the running host (<c>open</c>, <c>status</c>, <c>stop</c>).</summary>
    public required string InstallSecret { get; init; }

    /// <summary>Base64 of 32 random bytes — the host's <c>Encryption:Key</c>.</summary>
    public required string EncryptionKey { get; init; }

    public override string ToString() => "LocalStackSecrets (hidden)";
}

/// <summary>
/// The local stack's own secrets, in the OS secret store under the reserved
/// <see cref="WorkspaceSecretStores.LocalStackScope"/> (Windows Credential Manager, macOS
/// Keychain, libsecret; the owner-only file store when the machine has none, or for a sandbox
/// <c>ORKKA_HOME</c>, or <c>ORKKA_SECRET_STORE=file</c>). Never written to a config file, never
/// printed, never put in the process environment: <c>./orkka start</c> hands them to the host in
/// memory.
/// </summary>
public sealed class LocalSecrets(IWorkspaceSecretStore store)
{
    public const string InstallSecretId = "install-secret";
    public const string EncryptionKeyId = "encryption-key";

    /// <summary>The proxy secret of the retired UI proxy (before 2026-11): deleted on start.</summary>
    internal const string LegacyProxySecretId = "proxy-secret";

    public static readonly IReadOnlyList<string> AllIds = [InstallSecretId, EncryptionKeyId];

    public static LocalSecrets Open(string orkkaRoot) =>
        new(WorkspaceSecretStores.CreateDefault(orkkaRoot, WorkspaceSecretStores.LocalStackScope));

    public string Description => store.Description;

    /// <summary>Every secret, generating the missing ones (and forgetting the retired proxy secret).</summary>
    public LocalStackSecrets EnsureAll()
    {
        try
        {
            if (store.Get(LegacyProxySecretId) is not null)
                store.Delete(LegacyProxySecretId);
        }
        catch (Exception)
        {
            // Best effort: an unreadable legacy entry is harmless.
        }

        return new()
        {
            InstallSecret = GetOrCreate(InstallSecretId, () => RandomToken(32)),
            EncryptionKey = GetOrCreate(EncryptionKeyId, () => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
        };
    }

    public string? InstallSecret => store.Get(InstallSecretId);

    public string? Get(string id) => store.Get(id);

    private string GetOrCreate(string id, Func<string> generate)
    {
        if (store.Get(id) is { Length: > 0 } existing)
            return existing;
        var value = generate();
        store.Set(id, value);
        return value;
    }

    /// <summary>URL-safe base64 without padding.</summary>
    internal static string RandomToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
