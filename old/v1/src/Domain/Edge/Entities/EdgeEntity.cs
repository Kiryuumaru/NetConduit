using Domain.Common.Models;
using Domain.Edge.Enums;

namespace Domain.Edge.Entities;

public class EdgeEntity : BaseAuditableEntity
{
    public required string Name { get; set; }

    public required EdgeType EdgeType { get; set; }

    public required byte[] Key { get; set; }
}
