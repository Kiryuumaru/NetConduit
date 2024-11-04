using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.PortRoute.Dtos;

public class PortRouteEditDto
{
    public byte[]? SourceEdgeId { get; init; }

    public int? SourceEdgePort { get; init; }

    public byte[]? DestinationEdgeId { get; init; }

    public int? DestinationEdgePort { get; init; }
}
