using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetConduit.Enums;
using NetConduit.Models;

namespace NetConduit.Internal;

/// <summary>
/// Binary encoder/decoder for delta operations.
/// More compact than JSON encoding for wire transmission.
/// </summary>
/// <remarks>
/// Wire format:
/// - Header: [version:1][opCount:varint]
/// - String table: [count:varint][strings...]
/// - Operations: [op...]
/// 
/// Each operation:
/// - [opCode:1][pathLength:varint][pathSegments...][value?][index?]
/// 
/// Path segments reference string table by index (varint).
/// Values are encoded as: [type:1][data...]
/// </remarks>
internal static class DeltaBinaryEncoder
{
    private const byte Version = 1;

    // Value type markers
    private const byte TypeNull = 0;
    private const byte TypeBool = 1;
    private const byte TypeInt = 2;
    private const byte TypeLong = 3;
    private const byte TypeDouble = 4;
    private const byte TypeString = 5;
    private const byte TypeArray = 6;
    private const byte TypeObject = 7;

    /// <summary>
    /// Encodes delta operations to binary format with path compression.
    /// </summary>
    public static byte[] Encode(List<DeltaOperation> ops)
    {
        using var ms = new MemoryStream();

        // Build string table from all paths
        var stringTable = BuildStringTable(ops);
        var stringToIndex = new Dictionary<string, int>();
        for (int i = 0; i < stringTable.Count; i++)
        {
            stringToIndex[stringTable[i]] = i;
        }

        // Write header
        ms.WriteByte(Version);
        WriteVarint(ms, ops.Count);

        // Write string table
        WriteVarint(ms, stringTable.Count);
        foreach (var s in stringTable)
        {
            WriteString(ms, s);
        }

        // Write operations
        foreach (var op in ops)
        {
            ms.WriteByte((byte)op.Op);

            // Write path
            WriteVarint(ms, op.Path.Length);
            foreach (var segment in op.Path)
            {
                if (segment is string str)
                {
                    ms.WriteByte(0); // String segment marker
                    WriteVarint(ms, stringToIndex[str]);
                }
                else if (segment is int idx)
                {
                    ms.WriteByte(1); // Int segment marker
                    WriteVarint(ms, idx);
                }
            }

            // Write value for operations that have one
            if (op.Op is DeltaOp.Set or DeltaOp.ArrayInsert or DeltaOp.ArrayReplace)
            {
                WriteJsonValue(ms, op.Value);
            }

            // Write index for array operations
            if (op.Index.HasValue)
            {
                WriteVarint(ms, op.Index.Value);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Decodes binary format back to delta operations.
    /// </summary>
    public static List<DeltaOperation> Decode(ReadOnlySpan<byte> data)
    {
        var ops = new List<DeltaOperation>();
        var offset = 0;

        // Read header
        var version = data[offset++];
        if (version != Version)
            throw new InvalidOperationException($"Unsupported binary delta version: {version}");

        var opCount = ReadVarint(data, ref offset);

        // Read string table
        var stringCount = ReadVarint(data, ref offset);
        var stringTable = new string[stringCount];
        for (int i = 0; i < stringCount; i++)
        {
            stringTable[i] = ReadString(data, ref offset);
        }

        // Read operations
        for (int i = 0; i < opCount; i++)
        {
            var opCode = (DeltaOp)data[offset++];

            // Read path
            var pathLength = ReadVarint(data, ref offset);
            var path = new object[pathLength];
            for (int j = 0; j < pathLength; j++)
            {
                var segmentType = data[offset++];
                if (segmentType == 0) // String
                {
                    var strIndex = ReadVarint(data, ref offset);
                    path[j] = stringTable[strIndex];
                }
                else // Int
                {
                    path[j] = ReadVarint(data, ref offset);
                }
            }

            // Read value
            JsonNode? value = null;
            if (opCode is DeltaOp.Set or DeltaOp.ArrayInsert or DeltaOp.ArrayReplace)
            {
                value = ReadJsonValue(data, ref offset);
            }

            // Read index
            int? index = null;
            if (opCode is DeltaOp.ArrayInsert or DeltaOp.ArrayRemove)
            {
                index = ReadVarint(data, ref offset);
            }

            ops.Add(new DeltaOperation(opCode, path, value, index));
        }

        return ops;
    }

    private static List<string> BuildStringTable(List<DeltaOperation> ops)
    {
        var strings = new HashSet<string>();
        foreach (var op in ops)
        {
            foreach (var segment in op.Path)
            {
                if (segment is string s)
                    strings.Add(s);
            }
            CollectStringsFromJson(op.Value, strings);
        }
        return strings.ToList();
    }

    private static void CollectStringsFromJson(JsonNode? node, HashSet<string> strings)
    {
        if (node is null) return;

        if (node is JsonObject obj)
        {
            foreach (var prop in obj)
            {
                strings.Add(prop.Key);
                CollectStringsFromJson(prop.Value, strings);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                CollectStringsFromJson(item, strings);
            }
        }
        else if (node is JsonValue val && val.TryGetValue<string>(out var s))
        {
            strings.Add(s);
        }
    }

    private static void WriteVarint(Stream ms, int value)
    {
        var uvalue = (uint)value;
        while (uvalue >= 0x80)
        {
            ms.WriteByte((byte)(uvalue | 0x80));
            uvalue >>= 7;
        }
        ms.WriteByte((byte)uvalue);
    }

    private static int ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        int result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = data[offset++];
            result |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return result;
    }

    private static void WriteString(Stream ms, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(ms, bytes.Length);
        ms.Write(bytes);
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        var length = ReadVarint(data, ref offset);
        var str = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return str;
    }

    private static void WriteJsonValue(Stream ms, JsonNode? value)
    {
        if (value is null)
        {
            ms.WriteByte(TypeNull);
            return;
        }

        if (value is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out var boolVal))
            {
                ms.WriteByte(TypeBool);
                ms.WriteByte(boolVal ? (byte)1 : (byte)0);
            }
            else if (jv.TryGetValue<int>(out var intVal))
            {
                ms.WriteByte(TypeInt);
                WriteVarint(ms, intVal);
            }
            else if (jv.TryGetValue<long>(out var longVal))
            {
                ms.WriteByte(TypeLong);
                var buf = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(buf, longVal);
                ms.Write(buf);
            }
            else if (jv.TryGetValue<double>(out var doubleVal))
            {
                ms.WriteByte(TypeDouble);
                var buf = new byte[8];
                BinaryPrimitives.WriteDoubleLittleEndian(buf, doubleVal);
                ms.Write(buf);
            }
            else if (jv.TryGetValue<string>(out var strVal))
            {
                ms.WriteByte(TypeString);
                WriteString(ms, strVal!);
            }
            else
            {
                // Fallback: serialize as JSON string
                ms.WriteByte(TypeString);
                WriteString(ms, jv.ToJsonString());
            }
        }
        else if (value is JsonArray arr)
        {
            ms.WriteByte(TypeArray);
            WriteVarint(ms, arr.Count);
            foreach (var item in arr)
            {
                WriteJsonValue(ms, item);
            }
        }
        else if (value is JsonObject obj)
        {
            ms.WriteByte(TypeObject);
            WriteVarint(ms, obj.Count);
            foreach (var prop in obj)
            {
                WriteString(ms, prop.Key);
                WriteJsonValue(ms, prop.Value);
            }
        }
    }

    private static JsonNode? ReadJsonValue(ReadOnlySpan<byte> data, ref int offset)
    {
        var type = data[offset++];

        return type switch
        {
            TypeNull => null,
            TypeBool => JsonValue.Create(data[offset++] != 0),
            TypeInt => JsonValue.Create(ReadVarint(data, ref offset)),
            TypeLong => ReadLong(data, ref offset),
            TypeDouble => ReadDouble(data, ref offset),
            TypeString => JsonValue.Create(ReadString(data, ref offset)),
            TypeArray => ReadJsonArray(data, ref offset),
            TypeObject => ReadJsonObject(data, ref offset),
            _ => throw new InvalidOperationException($"Unknown value type: {type}")
        };
    }

    private static JsonValue ReadLong(ReadOnlySpan<byte> data, ref int offset)
    {
        var val = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
        offset += 8;
        return JsonValue.Create(val);
    }

    private static JsonValue ReadDouble(ReadOnlySpan<byte> data, ref int offset)
    {
        var val = BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(offset, 8));
        offset += 8;
        return JsonValue.Create(val);
    }

    private static JsonArray ReadJsonArray(ReadOnlySpan<byte> data, ref int offset)
    {
        var count = ReadVarint(data, ref offset);
        var arr = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            arr.Add(ReadJsonValue(data, ref offset));
        }
        return arr;
    }

    private static JsonObject ReadJsonObject(ReadOnlySpan<byte> data, ref int offset)
    {
        var count = ReadVarint(data, ref offset);
        var obj = new JsonObject();
        for (int i = 0; i < count; i++)
        {
            var key = ReadString(data, ref offset);
            var val = ReadJsonValue(data, ref offset);
            obj[key] = val;
        }
        return obj;
    }
}
