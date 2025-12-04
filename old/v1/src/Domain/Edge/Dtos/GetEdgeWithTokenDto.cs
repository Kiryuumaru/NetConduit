namespace Domain.Edge.Dtos;

public class GetEdgeWithTokenDto : GetEdgeInfoDto
{
    public required string Token { get; init; }
}
