namespace Domain.PortRoute.Dtos;

public class AddPortRouteDto
{
    public required Guid SourceEdgeId { get; init; }

    public required int SourceEdgePort { get; init; }

    public required Guid DestinationEdgeId { get; init; }

    public required int DestinationEdgePort { get; init; }
}
