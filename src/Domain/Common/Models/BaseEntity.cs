namespace Domain.Common.Models;

public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid Rev { get; set; } = Guid.NewGuid();
}
