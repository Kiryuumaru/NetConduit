namespace Domain.PortRoute.Dtos;

public class EditPortRouteDto
{
    public Guid? SourceEdgeId { get; init; }

    public int? SourceEdgePort { get; init; }

    public Guid? DestinationEdgeId { get; init; }

    public int? DestinationEdgePort { get; init; }
}
