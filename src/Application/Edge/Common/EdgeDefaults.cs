using Domain.Edge.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.Common;

public static class EdgeDefaults
{
    public static readonly Guid ServerEdgeId = Guid.Empty;

    public const string ServerEdgeName = "server";

    public const int EdgeKeySize = 64;

    public const int EdgeCommsBufferSize = 16384;

    public static readonly Guid MockChannelKey = new("00000000-0000-0000-0000-000000001234");
}
