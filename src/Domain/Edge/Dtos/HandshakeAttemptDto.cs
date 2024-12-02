using Domain.Edge.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Dtos;

public class HandshakeAttemptDto
{
    public required byte[]? PublicKey { get; init; }

    public required byte[]? EncryptedEdgeToken { get; init; }

    public required byte[]? EncryptedHandshakeToken { get; init; }
}
