using Domain.PortRoute.Entities;
using Microsoft.AspNetCore.Routing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamLine.Common;

public class IncomingStreamLine(PortRouteEntity route) : BaseStreamLine(route.ToEdgePort)
{
    public PortRouteEntity Route { get; } = route;
}
