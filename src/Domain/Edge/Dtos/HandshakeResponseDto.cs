namespace Domain.Edge.Dtos;

public class HandshakeResponseDto
{
    public required byte[]? PublicKey { get; init; }

    public required byte[]? EncryptedAcceptedEdgeKey { get; init; }
}
