using Domain.Edge.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.Common;

public static class EdgeDefaults
{
    public const int EdgeKeySize = 64;

    public const int EdgeCommsBufferSize = 16384;

    public const int EdgeHandshakeRequestLength = 4096;

    public const int EdgeHandshakeRSABitsLength = 4096;

    public static readonly Guid HandshakeChannel = new("00000000-0000-0000-0000-000000000001");

    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    public static readonly Guid MockChannelKey0 = new("00000000-0000-0000-0000-000000001000");
    public static readonly Guid MockChannelKey1 = new("00000000-0000-0000-0000-000000001001");
    public static readonly Guid MockChannelKey2 = new("00000000-0000-0000-0000-000000001002");
    public static readonly Guid MockChannelKey3 = new("00000000-0000-0000-0000-000000001003");
    public static readonly Guid MockChannelKey4 = new("00000000-0000-0000-0000-000000001004");
    public static readonly Guid MockChannelKey5 = new("00000000-0000-0000-0000-000000001005");
    public static readonly Guid MockChannelKey6 = new("00000000-0000-0000-0000-000000001006");
    public static readonly Guid MockChannelKey7 = new("00000000-0000-0000-0000-000000001007");
    public static readonly Guid MockChannelKey8 = new("00000000-0000-0000-0000-000000001008");
    public static readonly Guid MockChannelKey9 = new("00000000-0000-0000-0000-000000001009");
    public static readonly Guid MockMsgChannelKey0 = new("00000000-0000-0000-0000-000000002000");
    public static readonly Guid MockMsgChannelKey1 = new("00000000-0000-0000-0000-000000002001");
    public static readonly Guid MockMsgChannelKey2 = new("00000000-0000-0000-0000-000000002002");
    public static readonly Guid MockMsgChannelKey3 = new("00000000-0000-0000-0000-000000002003");
    public static readonly Guid MockMsgChannelKey4 = new("00000000-0000-0000-0000-000000002004");
    public static readonly Guid MockMsgChannelKey5 = new("00000000-0000-0000-0000-000000002005");
    public static readonly Guid MockMsgChannelKey6 = new("00000000-0000-0000-0000-000000002006");
    public static readonly Guid MockMsgChannelKey7 = new("00000000-0000-0000-0000-000000002007");
    public static readonly Guid MockMsgChannelKey8 = new("00000000-0000-0000-0000-000000002008");
    public static readonly Guid MockMsgChannelKey9 = new("00000000-0000-0000-0000-000000002009");
}
