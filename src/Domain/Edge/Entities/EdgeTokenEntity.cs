using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Entities;

public class EdgeTokenEntity : EdgeEntity
{
    public required string Token { get; init; }
}
