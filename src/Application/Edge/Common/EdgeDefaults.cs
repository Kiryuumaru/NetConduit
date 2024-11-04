using Domain.Edge.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.Common;

public static class EdgeDefaults
{
    public static EdgeInfo ServerEdgeInfo { get; } = new()
    {
        Id = "server",
        Name = "Server"
    };
}
