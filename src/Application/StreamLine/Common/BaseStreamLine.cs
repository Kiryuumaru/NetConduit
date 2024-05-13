using DisposableHelpers.Attributes;
using Domain.PortRoute.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamLine.Common;

[Disposable]
public abstract partial class BaseStreamLine(PortRouteEntity route)
{
    public PortRouteEntity Route { get; } = route;
}
