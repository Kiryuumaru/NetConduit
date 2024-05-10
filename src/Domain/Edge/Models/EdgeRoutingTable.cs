using Domain.Edge.Entities;
using Domain.PortRoute.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Models;

public class EdgeRoutingTable : EdgeConnectionEntity
{
    public required Dictionary<string, PortRouteEntity> Table { get; init; }

    public required Dictionary<string, EdgeEntity> Edges { get; init; }
}
