using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.PortRoute.Entities;

public class PortRouteEntity
{
    public required string Id { get; init; }

    public required byte[] SourceEdgeId { get; init; }

    public required int SourceEdgePort { get; init; }

    public required byte[] DestinationEdgeId { get; init; }

    public required int DestinationEdgePort { get; init; }
}
