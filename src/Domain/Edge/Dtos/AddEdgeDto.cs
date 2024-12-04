using Domain.Edge.Enums;

namespace Domain.Edge.Dtos;

public class AddEdgeDto
{
    public required EdgeType EdgeType { get; init; }

    public required string Name { get; init; }
}
