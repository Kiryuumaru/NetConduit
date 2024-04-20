using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Route.Entities;

public class PortRouteEntity
{
    public required string FromClientId { get; init; }

    public required int FromClientPort { get; init; }

    public required string ToClientId { get; init; }

    public required int ToClientPort { get; init; }
}
