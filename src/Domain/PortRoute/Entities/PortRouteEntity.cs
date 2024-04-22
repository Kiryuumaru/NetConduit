using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.PortRoute.Entities;

public class PortRouteEntity
{
    public required string Id { get; init; }

    public required string FromEdgeId { get; init; }

    public required int FromEdgePort { get; init; }

    public required string ToEdgeId { get; init; }

    public required int ToEdgePort { get; init; }
}
