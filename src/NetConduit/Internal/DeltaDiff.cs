using System.Text.Json.Nodes;
using NetConduit.Enums;
using NetConduit.Models;

namespace NetConduit.Internal;

/// <summary>
/// Computes delta between two JSON states.
/// </summary>
internal static class DeltaDiff
{
    /// <summary>
    /// Computes the delta operations needed to transform oldState into newState.
    /// </summary>
    public static List<DeltaOperation> ComputeDelta(JsonNode? oldState, JsonNode? newState)
    {
        if (oldState is null || newState is null)
            return [];

        if (oldState is JsonObject oldObj && newState is JsonObject newObj)
            return DiffObjects(oldObj, newObj, []);

        if (oldState is JsonArray oldArr && newState is JsonArray newArr)
            return DiffArrays(oldArr, newArr, []);

        // Primitive values - single Set if different
        if (!DeepEquals(oldState, newState))
            return [new DeltaOperation(DeltaOp.Set, [], newState?.DeepClone())];

        return [];
    }

    /// <summary>
    /// Computes delta operations for two JSON objects.
    /// </summary>
    internal static List<DeltaOperation> DiffObjects(JsonObject oldObj, JsonObject newObj, object[] basePath)
    {
        var ops = new List<DeltaOperation>();

        // Check properties in new state
        foreach (var prop in newObj)
        {
            var path = CreatePath(basePath, prop.Key);
            var oldValue = oldObj[prop.Key];
            var newValue = prop.Value;

            if (oldValue is null && newValue is not null)
            {
                // Property added
                ops.Add(new DeltaOperation(DeltaOp.Set, path, newValue?.DeepClone()));
            }
            else if (newValue is null && oldValue is not null)
            {
                // Property explicitly set to null
                ops.Add(new DeltaOperation(DeltaOp.SetNull, path));
            }
            else if (oldValue is not null && newValue is not null && !DeepEquals(oldValue, newValue))
            {
                // Property changed - recurse for objects/arrays
                if (newValue is JsonObject newChildObj && oldValue is JsonObject oldChildObj)
                {
                    ops.AddRange(DiffObjects(oldChildObj, newChildObj, path));
                }
                else if (newValue is JsonArray newChildArr && oldValue is JsonArray oldChildArr)
                {
                    ops.AddRange(DiffArrays(oldChildArr, newChildArr, path));
                }
                else
                {
                    ops.Add(new DeltaOperation(DeltaOp.Set, path, newValue?.DeepClone()));
                }
            }
        }

        // Check for removed properties
        foreach (var prop in oldObj)
        {
            if (!newObj.ContainsKey(prop.Key))
            {
                ops.Add(new DeltaOperation(DeltaOp.Remove, CreatePath(basePath, prop.Key)));
            }
        }

        return ops;
    }

    /// <summary>
    /// Computes delta operations for two JSON arrays using LCS algorithm.
    /// </summary>
    internal static List<DeltaOperation> DiffArrays(JsonArray oldArr, JsonArray newArr, object[] basePath)
    {
        var ops = new List<DeltaOperation>();
        var lcs = ComputeLCS(oldArr, newArr);

        int oldIdx = 0, newIdx = 0, lcsIdx = 0;

        while (oldIdx < oldArr.Count || newIdx < newArr.Count)
        {
            bool oldMatchesLcs = lcsIdx < lcs.Count && oldIdx < oldArr.Count && DeepEquals(oldArr[oldIdx], lcs[lcsIdx]);
            bool newMatchesLcs = lcsIdx < lcs.Count && newIdx < newArr.Count && DeepEquals(newArr[newIdx], lcs[lcsIdx]);

            if (oldMatchesLcs && newMatchesLcs)
            {
                // Both match LCS - element unchanged
                oldIdx++;
                newIdx++;
                lcsIdx++;
            }
            else if (newIdx < newArr.Count && !newMatchesLcs)
            {
                // New element not in LCS - it's an insertion
                ops.Add(new DeltaOperation(DeltaOp.ArrayInsert, basePath, newArr[newIdx]?.DeepClone(), newIdx));
                newIdx++;
            }
            else if (oldIdx < oldArr.Count && !oldMatchesLcs)
            {
                // Old element not in LCS - it's a removal
                // The index to remove is the current newIdx (position in result array)
                ops.Add(new DeltaOperation(DeltaOp.ArrayRemove, basePath, Index: newIdx));
                oldIdx++;
            }
        }

        // Fallback: if ops would be larger than full array replacement, replace entirely
        if (ops.Count > newArr.Count + 1)
        {
            return [new DeltaOperation(DeltaOp.ArrayReplace, basePath, newArr.DeepClone())];
        }

        return ops;
    }

    /// <summary>
    /// Computes the Longest Common Subsequence of two arrays.
    /// </summary>
    internal static List<JsonNode?> ComputeLCS(JsonArray a, JsonArray b)
    {
        int m = a.Count;
        int n = b.Count;

        // DP table
        var dp = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                if (DeepEquals(a[i - 1], b[j - 1]))
                {
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                }
                else
                {
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }
        }

        // Backtrack to find LCS
        var lcs = new List<JsonNode?>();
        int x = m, y = n;

        while (x > 0 && y > 0)
        {
            if (DeepEquals(a[x - 1], b[y - 1]))
            {
                lcs.Add(a[x - 1]?.DeepClone());
                x--;
                y--;
            }
            else if (dp[x - 1, y] > dp[x, y - 1])
            {
                x--;
            }
            else
            {
                y--;
            }
        }

        lcs.Reverse();
        return lcs;
    }

    /// <summary>
    /// Deep equality comparison for JsonNode values.
    /// </summary>
    internal static bool DeepEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        if (a is JsonValue valA && b is JsonValue valB)
        {
            return valA.ToJsonString() == valB.ToJsonString();
        }

        if (a is JsonObject objA && b is JsonObject objB)
        {
            if (objA.Count != objB.Count) return false;

            foreach (var prop in objA)
            {
                if (!objB.TryGetPropertyValue(prop.Key, out var bValue))
                    return false;
                if (!DeepEquals(prop.Value, bValue))
                    return false;
            }

            return true;
        }

        if (a is JsonArray arrA && b is JsonArray arrB)
        {
            if (arrA.Count != arrB.Count) return false;

            for (int i = 0; i < arrA.Count; i++)
            {
                if (!DeepEquals(arrA[i], arrB[i]))
                    return false;
            }

            return true;
        }

        return false;
    }

    private static object[] CreatePath(object[] basePath, string key)
    {
        var result = new object[basePath.Length + 1];
        Array.Copy(basePath, result, basePath.Length);
        result[basePath.Length] = key;
        return result;
    }
}
