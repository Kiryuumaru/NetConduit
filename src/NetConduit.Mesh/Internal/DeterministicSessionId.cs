using System.Security.Cryptography;
using System.Text;

namespace NetConduit.Mesh.Internal;

/// <summary>
/// Produces a deterministic <see cref="Guid"/> identifying a routed session between two nodes
/// for a given multiplexer ID. Both ends arrive at the same value without coordination.
/// </summary>
/// <remarks>
/// Algorithm: <c>SHA-256(min(nodeA, nodeB) || 0x00 || max(nodeA, nodeB) || 0x00 || multiplexerId)</c>;
/// the first 16 bytes are interpreted as a Guid. Inputs are UTF-8 encoded. Mesh ID validation
/// rejects the null byte separator, so the encoding is injective.
/// </remarks>
internal static class DeterministicSessionId
{
    internal static Guid Compute(string nodeA, string nodeB, string multiplexerId)
    {
        ArgumentNullException.ThrowIfNull(nodeA);
        ArgumentNullException.ThrowIfNull(nodeB);
        ArgumentNullException.ThrowIfNull(multiplexerId);

        string first;
        string second;
        if (string.CompareOrdinal(nodeA, nodeB) <= 0)
        {
            first = nodeA;
            second = nodeB;
        }
        else
        {
            first = nodeB;
            second = nodeA;
        }

        int byteCount =
            Encoding.UTF8.GetByteCount(first) + 1 +
            Encoding.UTF8.GetByteCount(second) + 1 +
            Encoding.UTF8.GetByteCount(multiplexerId);

        byte[] buffer = new byte[byteCount];
        int offset = 0;
        offset += Encoding.UTF8.GetBytes(first, 0, first.Length, buffer, offset);
        buffer[offset++] = 0;
        offset += Encoding.UTF8.GetBytes(second, 0, second.Length, buffer, offset);
        buffer[offset++] = 0;
        Encoding.UTF8.GetBytes(multiplexerId, 0, multiplexerId.Length, buffer, offset);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);

        return new Guid(hash[..16]);
    }
}
