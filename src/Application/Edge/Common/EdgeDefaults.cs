using Domain.Edge.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.Common;

public static class EdgeDefaults
{
    public static Guid ServerEdgeId { get; } = Guid.Empty;

    public static string ServerEdgeName { get; } = "server";

    public static int EdgeKeySize { get; } = 64;

    public static int EdgeCommsBufferSize { get; } = 16384;
}
