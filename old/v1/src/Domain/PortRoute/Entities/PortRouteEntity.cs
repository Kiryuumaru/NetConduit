using Domain.Common.Models;

namespace Domain.PortRoute.Entities;

public class PortRouteEntity : BaseAuditableEntity
{
    public required Guid SourceEdgeId { get; init; }

    public required int SourceEdgePort { get; init; }

    public required Guid DestinationEdgeId { get; init; }

    public required int DestinationEdgePort { get; init; }
}
