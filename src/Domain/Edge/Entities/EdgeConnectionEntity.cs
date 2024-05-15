using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Entities;

public class EdgeConnectionEntity : EdgeEntity
{
    public required string HandshakeToken { get; init; }
}
