using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.PortRoute.Dtos;

public class PortRouteEditDto
{
    public string? FromEdgeId { get; init; }

    public int? FromEdgePort { get; init; }

    public string? ToEdgeId { get; init; }

    public int? ToEdgePort { get; init; }
}
