using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NetConduit.Mesh.Internal;

/// <summary>
/// Length-prefixed JSON wire format for the topology channel.
///
/// Frame layout:
///   uint32 big-endian — total payload length in bytes (must be &gt; 0 and ≤ <c>maxMessageSize</c>)
///   bytes  payload   — UTF-8 JSON
///
/// JSON schema:
///   { "v": 1, "nodes": [ { "id": "A", "ver": 5, "pool": "us"|null, "neighbors": ["B","C"] }, ... ] }
///
/// Hand-rolled to stay trim and AOT-safe (no source-generated context required).
/// </summary>
internal static class TopologyWireFormat
{
    internal const int CurrentVersion = 1;

    /// <summary>Encode an adjacency snapshot to a length-prefixed UTF-8 JSON frame.</summary>
    internal static byte[] Encode(IReadOnlyCollection<TopologyEntry> entries)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", CurrentVersion);
            writer.WriteStartArray("nodes");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("id", entry.NodeId);
                writer.WriteNumber("ver", entry.Version);
                if (entry.PoolId is null)
                {
                    writer.WriteNull("pool");
                }
                else
                {
                    writer.WriteString("pool", entry.PoolId);
                }
                writer.WriteStartArray("neighbors");
                foreach (string neighbor in entry.Neighbors)
                {
                    writer.WriteStringValue(neighbor);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        byte[] payload = buffer.ToArray();
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)payload.Length);
        payload.AsSpan().CopyTo(frame.AsSpan(4));
        return frame;
    }

    /// <summary>
    /// Read a single length-prefixed JSON frame from the stream and decode it. Returns the entries.
    /// Throws on malformed input, oversized prefix, or premature EOF.
    /// </summary>
    internal static async Task<List<TopologyEntry>> ReadFrameAsync(
        Stream stream, int maxMessageSize, CancellationToken ct)
    {
        byte[] lengthBuf = new byte[4];
        await ReadExactAsync(stream, lengthBuf, ct).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthBuf);

        if (length == 0)
        {
            throw new InvalidDataException("Topology frame length must be > 0.");
        }
        if (length > (uint)maxMessageSize)
        {
            throw new InvalidDataException(
                $"Topology frame length {length} exceeds maximum {maxMessageSize}.");
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            await ReadExactAsync(stream, rented.AsMemory(0, (int)length), ct).ConfigureAwait(false);
            return DecodePayload(rented.AsSpan(0, (int)length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Decode a JSON payload (no length prefix) into entries.</summary>
    internal static List<TopologyEntry> DecodePayload(ReadOnlySpan<byte> payload)
    {
        var entries = new List<TopologyEntry>();
        var reader = new Utf8JsonReader(payload, isFinalBlock: true, default);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException("Topology JSON must start with an object.");
        }

        int? wireVersion = null;
        bool sawNodes = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new InvalidDataException("Unexpected token in topology root.");
            }

            string propName = reader.GetString() ?? throw new InvalidDataException("Null property name.");
            if (!reader.Read())
            {
                throw new InvalidDataException("Truncated topology JSON.");
            }

            switch (propName)
            {
                case "v":
                    if (reader.TokenType != JsonTokenType.Number)
                    {
                        throw new InvalidDataException("'v' must be a number.");
                    }
                    wireVersion = reader.GetInt32();
                    break;
                case "nodes":
                    sawNodes = true;
                    ReadNodes(ref reader, entries);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (wireVersion is null)
        {
            throw new InvalidDataException("Topology JSON missing 'v'.");
        }
        if (wireVersion != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported topology wire version {wireVersion}.");
        }
        if (!sawNodes)
        {
            throw new InvalidDataException("Topology JSON missing 'nodes'.");
        }

        return entries;
    }

    private static void ReadNodes(ref Utf8JsonReader reader, List<TopologyEntry> entries)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new InvalidDataException("'nodes' must be an array.");
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return;
            }
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException("Each node entry must be an object.");
            }

            string? id = null;
            long? version = null;
            string? pool = null;
            List<string> neighbors = new();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new InvalidDataException("Unexpected token in node entry.");
                }

                string fieldName = reader.GetString() ?? throw new InvalidDataException("Null property name.");
                if (!reader.Read())
                {
                    throw new InvalidDataException("Truncated node entry.");
                }

                switch (fieldName)
                {
                    case "id":
                        id = reader.GetString();
                        break;
                    case "ver":
                        version = reader.GetInt64();
                        break;
                    case "pool":
                        pool = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                        break;
                    case "neighbors":
                        if (reader.TokenType != JsonTokenType.StartArray)
                        {
                            throw new InvalidDataException("'neighbors' must be an array.");
                        }
                        while (reader.Read())
                        {
                            if (reader.TokenType == JsonTokenType.EndArray)
                            {
                                break;
                            }
                            if (reader.TokenType != JsonTokenType.String)
                            {
                                throw new InvalidDataException("Each neighbor must be a string.");
                            }
                            string? n = reader.GetString();
                            if (!string.IsNullOrEmpty(n))
                            {
                                neighbors.Add(n);
                            }
                        }
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (string.IsNullOrEmpty(id))
            {
                throw new InvalidDataException("Node entry missing 'id'.");
            }
            if (version is null || version < 0)
            {
                throw new InvalidDataException("Node entry missing or negative 'ver'.");
            }

            entries.Add(new TopologyEntry(id, version.Value, pool, neighbors));
        }

        throw new InvalidDataException("Unterminated 'nodes' array.");
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int got = await stream.ReadAsync(buffer.Slice(read), ct).ConfigureAwait(false);
            if (got == 0)
            {
                throw new EndOfStreamException(
                    $"Topology stream closed after {read} of {buffer.Length} bytes.");
            }
            read += got;
        }
    }
}

/// <summary>A single node's state in a topology message.</summary>
internal sealed record TopologyEntry(string NodeId, long Version, string? PoolId, IReadOnlyList<string> Neighbors);
