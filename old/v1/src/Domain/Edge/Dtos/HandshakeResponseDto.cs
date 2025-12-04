namespace Domain.Edge.Dtos;

public class HandshakeResponseDto
{
    public required byte[]? PublicKey { get; init; }

    public required byte[]? EncryptedAcceptedEdgeToken { get; init; }
}
