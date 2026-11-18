namespace Orkka.Modules.Agents.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Domain;
using Orkka.SharedKernel.Persistence;
using Orkka.SharedKernel.Security;

public sealed class AgentsDbContext(
    DbContextOptions<AgentsDbContext> options,
    ICurrentUser? currentUser = null) : ModuleDbContext(options, currentUser)
{
    public const string Module = "agents";
    public override string ModuleName => Module;

    public DbSet<ProjectAgent> ProjectAgents => Set<ProjectAgent>();
    public DbSet<AgentConversation> AgentConversations => Set<AgentConversation>();
    public DbSet<AgentMessage> AgentMessages => Set<AgentMessage>();
    public DbSet<ProjectMemory> ProjectMemories => Set<ProjectMemory>();
    public DbSet<AgentSkill> AgentSkills => Set<AgentSkill>();
    public DbSet<AgentSchedule> AgentSchedules => Set<AgentSchedule>();
    public DbSet<AgentScheduleRun> AgentScheduleRuns => Set<AgentScheduleRun>();
    public DbSet<DesignAsset> DesignAssets => Set<DesignAsset>();
    public DbSet<BrandKit> BrandKits => Set<BrandKit>();
    public DbSet<ChatAttachment> ChatAttachments => Set<ChatAttachment>();
    public DbSet<ArchitectureDiagram> ArchitectureDiagrams => Set<ArchitectureDiagram>();
    public DbSet<ArchitectureDiagramVersion> ArchitectureDiagramVersions => Set<ArchitectureDiagramVersion>();
    public DbSet<AgentAuditEvent> AgentAuditEvents => Set<AgentAuditEvent>();

    // Platform-global (no OrgId) — the admin-managed model catalog and the per-provider tier
    // designations the system picks from.
    public DbSet<AiModel> AiModels => Set<AiModel>();
    public DbSet<AiTierDefault> AiTierDefaults => Set<AiTierDefault>();

    // The audit log is append-only: reject any attempt to modify or delete an existing event so a
    // compromised path (or a bug) cannot tamper with the security record.
    protected override void OnBeforeSave()
    {
        foreach (var entry in ChangeTracker.Entries<AgentAuditEvent>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException(
                    "AgentAuditEvent is append-only and cannot be modified or deleted.");

            // Chain hash over the stamped row (CreatedAt is set just before this hook). The
            // writer already linked PreviousHash under the per-org lock.
            if (entry.State == EntityState.Added && entry.Entity.EntryHash is null)
                entry.Entity.EntryHash = AuditHash.Compute(entry.Entity);
        }
    }
}
