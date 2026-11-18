namespace Orkka.SharedKernel.Domain;



public abstract class BaseEntity : IAuditableEntity
{
    public Ulid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
