namespace Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Projects.Domain;
using Orkka.SharedKernel.Persistence;
using Orkka.SharedKernel.Security;

public sealed class ProjectsDbContext(
    DbContextOptions<ProjectsDbContext> options,
    ICurrentUser? currentUser = null) : ModuleDbContext(options, currentUser)
{
    public const string Module = "projects";
    public override string ModuleName => Module;

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectRepo> ProjectRepos => Set<ProjectRepo>();
    public DbSet<ProjectRunConfig> ProjectRunConfigs => Set<ProjectRunConfig>();
}
