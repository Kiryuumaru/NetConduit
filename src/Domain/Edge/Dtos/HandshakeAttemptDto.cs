namespace Domain.Edge.Dtos;

public class HandshakeAttemptDto
{
    public required byte[]? PublicKey { get; init; }

    public required byte[]? EncryptedEdgeToken { get; init; }

    public required byte[]? EncryptedHandshakeToken { get; init; }
}
