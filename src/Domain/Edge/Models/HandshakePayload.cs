using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Models;

public class HandshakePayload
{
    public required string MockMessage { get; init; }
}
