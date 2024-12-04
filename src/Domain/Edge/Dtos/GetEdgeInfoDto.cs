using Domain.Edge.Enums;

namespace Domain.Edge.Dtos;

public class GetEdgeInfoDto
{
    public required Guid Id { get; init; }

    public required EdgeType EdgeType { get; set; }

    public required string Name { get; init; }
}
