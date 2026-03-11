using NetConduit.Enums;
using NetConduit.Models;

namespace NetConduit.Internal;

/// <summary>
/// Compresses paths in delta operations by building a shared path table.
/// Useful when the same paths appear repeatedly across operations.
/// </summary>
/// <remarks>
/// Path compression works by:
/// 1. Building a table of unique path prefixes
/// 2. Replacing full paths with (tableIndex, remainingSegments)
/// 3. Decompression reconstructs full paths from table references
/// </remarks>
internal sealed class DeltaPathCompressor
{
    private readonly List<object[]> _pathTable = [];
    private readonly Dictionary<string, int> _pathToIndex = [];

    /// <summary>
    /// Compresses paths in operations by replacing common prefixes with table references.
    /// Returns compressed operations and the path table needed for decompression.
    /// </summary>
    public (List<CompressedOperation> Ops, List<object[]> PathTable) Compress(List<DeltaOperation> ops)
    {
        _pathTable.Clear();
        _pathToIndex.Clear();

        // Build path table from all paths
        foreach (var op in ops)
        {
            AddPathToTable(op.Path);
        }

        // Compress operations
        var compressed = new List<CompressedOperation>();
        foreach (var op in ops)
        {
            var (tableIndex, remaining) = FindBestMatch(op.Path);
            compressed.Add(new CompressedOperation(op.Op, tableIndex, remaining, op.Value, op.Index));
        }

        return (compressed, _pathTable.ToList());
    }

    /// <summary>
    /// Decompresses operations using the path table.
    /// </summary>
    public static List<DeltaOperation> Decompress(List<CompressedOperation> compressed, List<object[]> pathTable)
    {
        var ops = new List<DeltaOperation>();

        foreach (var c in compressed)
        {
            object[] fullPath;
            if (c.PathTableIndex < 0)
            {
                // No table reference, remaining is the full path
                fullPath = c.RemainingPath;
            }
            else
            {
                // Combine table entry with remaining segments
                var tableEntry = pathTable[c.PathTableIndex];
                fullPath = new object[tableEntry.Length + c.RemainingPath.Length];
                tableEntry.CopyTo(fullPath, 0);
                c.RemainingPath.CopyTo(fullPath, tableEntry.Length);
            }

            ops.Add(new DeltaOperation(c.Op, fullPath, c.Value, c.Index));
        }

        return ops;
    }

    /// <summary>
    /// Estimates the compression ratio for the given operations.
    /// Returns a value between 0 and 1 where lower is better compression.
    /// </summary>
    public static double EstimateCompressionRatio(List<DeltaOperation> ops)
    {
        if (ops.Count == 0) return 1.0;

        // Count total path segments before compression
        var totalSegments = ops.Sum(o => o.Path.Length);
        if (totalSegments == 0) return 1.0;

        // Count unique path prefixes
        var uniquePaths = new HashSet<string>();
        foreach (var op in ops)
        {
            var pathKey = PathToKey(op.Path);
            uniquePaths.Add(pathKey);

            // Also add all prefixes
            for (int len = 1; len < op.Path.Length; len++)
            {
                uniquePaths.Add(PathToKey(op.Path[..len]));
            }
        }

        // Estimate compressed size
        // Each op needs: tableIndex + remaining segments
        // vs original: all segments
        var estimatedCompressed = ops.Count + uniquePaths.Count;
        var original = totalSegments;

        return Math.Min(1.0, (double)estimatedCompressed / original);
    }

    private void AddPathToTable(object[] path)
    {
        // Add full path and all prefixes to consider
        for (int len = 1; len <= path.Length; len++)
        {
            var prefix = path[..len];
            var key = PathToKey(prefix);
            if (!_pathToIndex.ContainsKey(key))
            {
                _pathToIndex[key] = _pathTable.Count;
                _pathTable.Add(prefix);
            }
        }
    }

    private (int TableIndex, object[] Remaining) FindBestMatch(object[] path)
    {
        // Find longest matching prefix in table
        for (int len = path.Length; len >= 1; len--)
        {
            var prefix = path[..len];
            var key = PathToKey(prefix);
            if (_pathToIndex.TryGetValue(key, out var index))
            {
                return (index, path[len..]);
            }
        }

        // No match found, store full path as remaining
        return (-1, path);
    }

    private static string PathToKey(object[] path)
    {
        return string.Join("\x00", path.Select(s => s.ToString()));
    }
}

/// <summary>
/// A delta operation with compressed path representation.
/// </summary>
internal readonly record struct CompressedOperation(
    DeltaOp Op,
    int PathTableIndex,
    object[] RemainingPath,
    System.Text.Json.Nodes.JsonNode? Value,
    int? Index);
