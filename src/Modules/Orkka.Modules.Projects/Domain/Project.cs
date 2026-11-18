namespace Orkka.Modules.Projects.Domain;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Domain;

public sealed class Project : TenantEntity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public required string Key { get; set; }
    public int LastTaskNumber { get; set; }
    public string? Description { get; set; }
    /// <summary>user id of the human who created the project — auto-seeded tasks inherit
    /// it as their creator (notifications recipient). Null for pre-existing projects.</summary>
    public string? CreatedByUserId { get; set; }
    public string? Settings { get; set; }

    /// <summary>The folder on the developer's machine the project was created from — a
    /// monorepo, or a folder of repositories (<see cref="BaseFolderShape"/>). Null for projects
    /// whose repositories were linked one by one.</summary>
    public string? BaseFolder { get; set; }

    public BaseFolderShape? BaseFolderShape { get; set; }

    // Navigation (org lives in the Organizations module; OrgId comes from TenantEntity)
    public ICollection<ProjectRepo> Repos { get; set; } = [];
}
