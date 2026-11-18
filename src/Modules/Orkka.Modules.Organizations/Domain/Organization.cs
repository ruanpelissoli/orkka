namespace Orkka.Modules.Organizations.Domain;

using Orkka.SharedKernel.Domain;

public sealed class Organization : BaseEntity
{
    /// <summary>The name an auto-provisioned workspace gets until it is renamed.</summary>
    public const string DefaultName = "Personal";

    public required string Name { get; set; }
    public required string Slug { get; set; }

    // Navigation (module-internal only; Projects live in the Projects module and
    // reference organizations by OrgId)
    public ICollection<OrgMember> Members { get; set; } = [];
}
