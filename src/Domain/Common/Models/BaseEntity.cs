namespace Domain.Common.Models;

public abstract class BaseEntity
{
    public required Guid Id { get; init; }

    public required Guid Rev { get; init; }
}
