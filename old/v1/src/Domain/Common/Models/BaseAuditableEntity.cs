namespace Domain.Common.Models;

public abstract class BaseAuditableEntity : BaseEntity
{
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastModified { get; set; } = DateTimeOffset.UtcNow;
}
