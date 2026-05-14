using System.Diagnostics.CodeAnalysis;

namespace NetConduit.Mesh.Internal;

/// <summary>
/// Build and parse the reserved <c>_mesh:</c> channel names used by the mesh on neighbor multiplexers.
/// </summary>
internal static class MeshChannelNaming
{
    /// <summary>Common prefix of every mesh-reserved channel ID.</summary>
    internal const string Prefix = "_mesh:";

    /// <summary>Outbound topology channel name on a neighbor mux.</summary>
    internal const string TopologyOutbound = "_mesh:topo>>";

    /// <summary>Inbound topology channel name on a neighbor mux.</summary>
    internal const string TopologyInbound = "_mesh:topo<<";

    private const string RoutePrefix = "_mesh:route:";
    private const string OutboundSuffix = ">>";
    private const string InboundSuffix = "<<";

    /// <summary>Build the outbound (writer) route channel name for the source-side of a routed pipe.</summary>
    internal static string BuildOutboundRoute(string targetNodeId, string sourceNodeId, string multiplexerId, long nonce)
    {
        return $"{RoutePrefix}{targetNodeId}:{sourceNodeId}/{multiplexerId}:{nonce}{OutboundSuffix}";
    }

    /// <summary>Build the inbound (reader) route channel name paired with <see cref="BuildOutboundRoute"/>.</summary>
    internal static string BuildInboundRoute(string targetNodeId, string sourceNodeId, string multiplexerId, long nonce)
    {
        return $"{RoutePrefix}{targetNodeId}:{sourceNodeId}/{multiplexerId}:{nonce}{InboundSuffix}";
    }

    /// <summary>Build the channel base (no suffix). Used for diagnostic logging.</summary>
    internal static string BuildRouteBase(string targetNodeId, string sourceNodeId, string multiplexerId, long nonce)
    {
        return $"{RoutePrefix}{targetNodeId}:{sourceNodeId}/{multiplexerId}:{nonce}";
    }

    /// <summary>
    /// Try parse a route channel name with the outbound suffix (<c>&gt;&gt;</c>).
    /// Mesh routes are only ever recognised on their outbound suffix; the inbound side is paired implicitly.
    /// </summary>
    internal static bool TryParseOutboundRoute(string channelId, [NotNullWhen(true)] out RouteChannelInfo? info)
    {
        info = null;

        if (channelId is null || !channelId.StartsWith(RoutePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!channelId.EndsWith(OutboundSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        // body = "target:source/multiplexerId:nonce"
        int bodyStart = RoutePrefix.Length;
        int bodyEnd = channelId.Length - OutboundSuffix.Length;
        if (bodyEnd <= bodyStart)
        {
            return false;
        }

        string body = channelId.Substring(bodyStart, bodyEnd - bodyStart);

        int targetSep = body.IndexOf(':');
        if (targetSep <= 0 || targetSep == body.Length - 1)
        {
            return false;
        }

        string targetNodeId = body.Substring(0, targetSep);

        int slash = body.IndexOf('/', targetSep + 1);
        if (slash <= targetSep + 1 || slash == body.Length - 1)
        {
            return false;
        }

        string sourceNodeId = body.Substring(targetSep + 1, slash - targetSep - 1);

        int nonceSep = body.LastIndexOf(':');
        if (nonceSep <= slash + 1 || nonceSep == body.Length - 1)
        {
            return false;
        }

        string multiplexerId = body.Substring(slash + 1, nonceSep - slash - 1);
        string nonceStr = body.Substring(nonceSep + 1);

        if (!long.TryParse(nonceStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long nonce) || nonce < 0)
        {
            return false;
        }

        if (targetNodeId.Length == 0 || sourceNodeId.Length == 0 || multiplexerId.Length == 0)
        {
            return false;
        }

        info = new RouteChannelInfo(targetNodeId, sourceNodeId, multiplexerId, nonce);
        return true;
    }

    /// <summary>Check whether a channel ID is reserved by the mesh.</summary>
    internal static bool IsReserved(string channelId)
    {
        return channelId is not null && channelId.StartsWith(Prefix, StringComparison.Ordinal);
    }
}

/// <summary>Parsed components of a route channel name.</summary>
internal sealed record RouteChannelInfo(
    string TargetNodeId,
    string SourceNodeId,
    string MultiplexerId,
    long Nonce);
