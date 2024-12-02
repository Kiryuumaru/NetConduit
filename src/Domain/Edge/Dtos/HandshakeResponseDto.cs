using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Dtos;

public class HandshakeResponseDto
{
    public required byte[] PublicKey { get; init; }

    public required string RequestAcknowledgedToken { get; init; }
}
