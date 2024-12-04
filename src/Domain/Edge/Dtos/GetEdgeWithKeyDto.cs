namespace Domain.Edge.Dtos;

public class GetEdgeWithKeyDto : GetEdgeInfoDto
{
    public required byte[] Key { get; init; }
}
