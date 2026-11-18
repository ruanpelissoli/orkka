namespace Orkka.SharedKernel.Domain;



public abstract class TenantEntity : BaseEntity, ITenantEntity
{
    public Ulid OrgId { get; set; }
}
