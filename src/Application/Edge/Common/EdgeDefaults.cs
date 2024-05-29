using Domain.Edge.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Server.Edge.Common;

public static class EdgeDefaults
{
    public static EdgeEntity ServerEdgeEntity { get; } = new()
    {
        Id = "server",
        Name = "Server"
    };
}
