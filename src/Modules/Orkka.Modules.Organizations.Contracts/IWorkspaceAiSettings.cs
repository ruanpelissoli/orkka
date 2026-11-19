namespace Orkka.Modules.Organizations.Contracts;

/// <summary>
/// The workspace's stored AI provider choice (a column on the organization row). Raw keys only:
/// what a key means — and which keys are valid — is the Agents module's business
/// (<c>WorkspaceAiProviders</c>). Reads and writes are by org id and ignore the ambient tenant,
/// because agent seeding and the startup roster backfill run outside a request.
/// </summary>
public interface IWorkspaceAiSettings
{
    /// <summary>The stored key, or null for Automatic (also for an unknown workspace).</summary>
    Task<string?> GetAiProviderAsync(Ulid orgId, CancellationToken ct = default);

    /// <summary>Stores <paramref name="key"/> (null = Automatic). False when the workspace does not exist.</summary>
    Task<bool> SetAiProviderAsync(Ulid orgId, string? key, CancellationToken ct = default);
}
