namespace Domain.Common.Models;

public abstract class BaseAuditableEntity : BaseEntity
{
    public required DateTimeOffset Created { get; init; }

    public required DateTimeOffset LastModified { get; init; }
}
