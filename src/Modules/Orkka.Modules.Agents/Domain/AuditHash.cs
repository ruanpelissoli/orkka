namespace Orkka.Modules.Agents.Domain;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The audit chain's hash function. Computed in <c>AgentsDbContext.OnBeforeSave</c> for every
/// newly added event (after CreatedAt is stamped) and re-computed by the verify endpoint. Any
/// change to the canonical input is a chain break for existing rows — bump the version prefix
/// and treat older rows accordingly rather than editing in place.
/// </summary>
public static class AuditHash
{
    public const string Version = "v1";

    public static string Compute(AgentAuditEvent e)
    {
        var canonical = string.Join("|",
            Version,
            e.PreviousHash ?? "",
            e.Id.ToString(),
            e.OrgId.ToString(),
            e.ProjectId?.ToString() ?? "",
            e.EventType.ToString(),
            e.Actor,
            e.ActorKind.ToString(),
            e.ActorUserId ?? "",
            e.Tool ?? "",
            e.Summary,
            e.MetadataJson ?? "",
            CanonicalTimestamp(e.CreatedAt));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>UTC at microsecond precision: fixed, so a hash never depends on how precisely a
    /// store keeps the instant.</summary>
    private static string CanonicalTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var truncated = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
        return truncated.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'");
    }
}
