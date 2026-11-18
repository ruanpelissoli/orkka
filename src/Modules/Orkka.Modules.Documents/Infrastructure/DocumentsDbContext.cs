namespace Orkka.Modules.Documents.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Domain;
using Orkka.SharedKernel.Persistence;
using Orkka.SharedKernel.Security;

public sealed class DocumentsDbContext(
    DbContextOptions<DocumentsDbContext> options,
    ICurrentUser? currentUser = null) : ModuleDbContext(options, currentUser)
{
    public const string Module = "documents";
    public override string ModuleName => Module;

    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();
    public DbSet<DocumentRevision> DocumentRevisions => Set<DocumentRevision>();
}
