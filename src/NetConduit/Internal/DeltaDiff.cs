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
    /// Computes delta operations for two JSON arrays.
    /// Prioritizes minimal delta size by detecting deep changes within array elements.
    /// </summary>
    internal static List<DeltaOperation> DiffArrays(JsonArray oldArr, JsonArray newArr, object[] basePath)
    {
        // Same length arrays: position-based deep diffing (most common case for state updates)
        if (oldArr.Count == newArr.Count)
        {
            return DiffArraysSameLength(oldArr, newArr, basePath);
        }

        // Different length arrays: use LCS for insert/remove detection
        return DiffArraysDifferentLength(oldArr, newArr, basePath);
    }

    /// <summary>
    /// Diffs arrays of the same length using position-based matching.
    /// Each element at index N is compared with the element at the same index.
    /// </summary>
    private static List<DeltaOperation> DiffArraysSameLength(JsonArray oldArr, JsonArray newArr, object[] basePath)
    {
        var ops = new List<DeltaOperation>();

        for (int i = 0; i < oldArr.Count; i++)
        {
            var oldElem = oldArr[i];
            var newElem = newArr[i];

            if (DeepEquals(oldElem, newElem))
                continue;

            // Recurse into objects/arrays for deep diffing
            if (oldElem is JsonObject oldObj && newElem is JsonObject newObj)
            {
                var path = CreatePath(basePath, i);
                ops.AddRange(DiffObjects(oldObj, newObj, path));
            }
            else if (oldElem is JsonArray oldChildArr && newElem is JsonArray newChildArr)
            {
                var path = CreatePath(basePath, i);
                ops.AddRange(DiffArrays(oldChildArr, newChildArr, path));
            }
            else
            {
                // Primitive value changed at index
                var path = CreatePath(basePath, i);
                if (newElem is null)
                {
                    ops.Add(new DeltaOperation(DeltaOp.SetNull, path));
                }
                else
                {
                    ops.Add(new DeltaOperation(DeltaOp.Set, path, newElem.DeepClone()));
                }
            }
        }

        return ops;
    }

    /// <summary>
    /// Diffs arrays of different lengths using smart matching.
    /// </summary>
    private static List<DeltaOperation> DiffArraysDifferentLength(JsonArray oldArr, JsonArray newArr, object[] basePath)
    {
        // Check if arrays contain objects (can use smart matching) or primitives (use simple LCS)
        var containsObjects = (oldArr.Count > 0 && oldArr[0] is JsonObject) || 
                             (newArr.Count > 0 && newArr[0] is JsonObject);

        if (containsObjects)
        {
            return DiffObjectArrays(oldArr, newArr, basePath);
        }
        else
        {
            return DiffPrimitiveArrays(oldArr, newArr, basePath);
        }
    }

    /// <summary>
    /// Diffs arrays of objects using ID/similarity matching with deep element diffs.
    /// </summary>
    private static List<DeltaOperation> DiffObjectArrays(JsonArray oldArr, JsonArray newArr, object[] basePath)
    {
        var ops = new List<DeltaOperation>();

        // Build match map: find best matches between old and new elements
        var (matchedPairs, unmatchedOld, unmatchedNew) = FindBestMatches(oldArr, newArr);

        // STEP 1: Process removals in reverse order (high to low) so indices remain valid
        // Each removal shifts subsequent elements, but working from the end avoids this issue
        var sortedRemovals = unmatchedOld.OrderByDescending(i => i).ToList();
        foreach (var oldIdx in sortedRemovals)
        {
            ops.Add(new DeltaOperation(DeltaOp.ArrayRemove, basePath, Index: oldIdx));
        }

        // STEP 2: Process insertions in order by newIdx
        foreach (var newIdx in unmatchedNew.OrderBy(i => i))
        {
            ops.Add(new DeltaOperation(DeltaOp.ArrayInsert, basePath, newArr[newIdx]?.DeepClone(), newIdx));
        }

        // STEP 3: Process matched pairs - compute deep diffs using final (newIdx) positions
        // After removals and insertions, the array structure matches, so newIdx is correct
        foreach (var (oldIdx, newIdx) in matchedPairs)
        {
            var oldElem = oldArr[oldIdx];
            var newElem = newArr[newIdx];

            if (!DeepEquals(oldElem, newElem))
            {
                if (oldElem is JsonObject oldObj && newElem is JsonObject newObj)
                {
                    var path = CreatePath(basePath, newIdx);
                    ops.AddRange(DiffObjects(oldObj, newObj, path));
                }
                else if (oldElem is JsonArray oldChildArr && newElem is JsonArray newChildArr)
                {
                    var path = CreatePath(basePath, newIdx);
                    ops.AddRange(DiffArrays(oldChildArr, newChildArr, path));
                }
                else if (newElem is null)
                {
                    ops.Add(new DeltaOperation(DeltaOp.SetNull, CreatePath(basePath, newIdx)));
                }
                else
                {
                    ops.Add(new DeltaOperation(DeltaOp.Set, CreatePath(basePath, newIdx), newElem.DeepClone()));
                }
            }
        }

        // Fallback: only use ArrayReplace when there are many operations
        // A single insert/remove is always more efficient than full replacement
        // Use replacement only when operations exceed array length (many changes)
        if (ops.Count > Math.Max(oldArr.Count, newArr.Count))
        {
            return [new DeltaOperation(DeltaOp.ArrayReplace, basePath, newArr.DeepClone())];
        }

        return ops;
    }

    /// <summary>
    /// Diffs arrays of primitives using LCS for optimal insert/remove detection.
    /// </summary>
    private static List<DeltaOperation> DiffPrimitiveArrays(JsonArray oldArr, JsonArray newArr, object[] basePath)
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
                ops.Add(new DeltaOperation(DeltaOp.ArrayRemove, basePath, Index: newIdx));
                oldIdx++;
            }
        }

        // Fallback: only use ArrayReplace when there are many operations
        // A single insert/remove is always more efficient than full replacement
        if (ops.Count > Math.Max(oldArr.Count, newArr.Count))
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
    /// Finds best matches between old and new array elements.
    /// Uses identity matching (id field) or structural similarity.
    /// </summary>
    private static (List<(int oldIdx, int newIdx)> matched, List<int> unmatchedOld, List<int> unmatchedNew) 
        FindBestMatches(JsonArray oldArr, JsonArray newArr)
    {
        var matched = new List<(int oldIdx, int newIdx)>();
        var usedOld = new HashSet<int>();
        var usedNew = new HashSet<int>();

        // Pass 1: Try to match by "id" field if objects have one
        for (int newIdx = 0; newIdx < newArr.Count; newIdx++)
        {
            var newId = GetIdValue(newArr[newIdx]);
            if (newId is null) continue;

            for (int oldIdx = 0; oldIdx < oldArr.Count; oldIdx++)
            {
                if (usedOld.Contains(oldIdx)) continue;

                var oldId = GetIdValue(oldArr[oldIdx]);
                if (oldId is not null && oldId == newId)
                {
                    matched.Add((oldIdx, newIdx));
                    usedOld.Add(oldIdx);
                    usedNew.Add(newIdx);
                    break;
                }
            }
        }

        // Pass 2: Match remaining by exact equality
        for (int newIdx = 0; newIdx < newArr.Count; newIdx++)
        {
            if (usedNew.Contains(newIdx)) continue;

            for (int oldIdx = 0; oldIdx < oldArr.Count; oldIdx++)
            {
                if (usedOld.Contains(oldIdx)) continue;

                if (DeepEquals(oldArr[oldIdx], newArr[newIdx]))
                {
                    matched.Add((oldIdx, newIdx));
                    usedOld.Add(oldIdx);
                    usedNew.Add(newIdx);
                    break;
                }
            }
        }

        // Pass 3: Match remaining objects by structural similarity (same keys)
        for (int newIdx = 0; newIdx < newArr.Count; newIdx++)
        {
            if (usedNew.Contains(newIdx)) continue;
            if (newArr[newIdx] is not JsonObject newObj) continue;

            int bestOldIdx = -1;
            double bestSimilarity = 0;

            for (int oldIdx = 0; oldIdx < oldArr.Count; oldIdx++)
            {
                if (usedOld.Contains(oldIdx)) continue;
                if (oldArr[oldIdx] is not JsonObject oldObj) continue;

                var similarity = ComputeSimilarity(oldObj, newObj);
                if (similarity > bestSimilarity && similarity >= 0.5) // At least 50% similar
                {
                    bestSimilarity = similarity;
                    bestOldIdx = oldIdx;
                }
            }

            if (bestOldIdx >= 0)
            {
                matched.Add((bestOldIdx, newIdx));
                usedOld.Add(bestOldIdx);
                usedNew.Add(newIdx);
            }
        }

        var unmatchedOld = Enumerable.Range(0, oldArr.Count).Where(i => !usedOld.Contains(i)).ToList();
        var unmatchedNew = Enumerable.Range(0, newArr.Count).Where(i => !usedNew.Contains(i)).ToList();

        return (matched, unmatchedOld, unmatchedNew);
    }

    /// <summary>
    /// Gets the value of an "id" field if present.
    /// </summary>
    private static string? GetIdValue(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;

        if (obj.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue idVal)
        {
            return idVal.ToString();
        }

        return null;
    }

    /// <summary>
    /// Computes structural similarity between two JSON objects (0.0 to 1.0).
    /// </summary>
    private static double ComputeSimilarity(JsonObject a, JsonObject b)
    {
        var keysA = a.Select(p => p.Key).ToHashSet();
        var keysB = b.Select(p => p.Key).ToHashSet();

        var intersection = keysA.Intersect(keysB).Count();
        var union = keysA.Union(keysB).Count();

        if (union == 0) return 1.0;

        var keySimilarity = (double)intersection / union;

        // Also check value similarity for matching keys
        int matchingValues = 0;
        foreach (var key in keysA.Intersect(keysB))
        {
            if (DeepEquals(a[key], b[key]))
                matchingValues++;
        }

        var valueSimilarity = intersection > 0 ? (double)matchingValues / intersection : 0;

        // Weight: 60% key structure, 40% value match
        return keySimilarity * 0.6 + valueSimilarity * 0.4;
    }

    /// <summary>
    /// Estimates the size of a JSON node for comparison.
    /// </summary>
    private static int EstimateNodeSize(JsonNode? node)
    {
        if (node is null) return 1;
        return node.ToJsonString().Length;
    }

    /// <summary>
    /// Estimates the total size of delta operations.
    /// </summary>
    private static int EstimateDeltaSize(List<DeltaOperation> ops)
    {
        int size = 0;
        foreach (var op in ops)
        {
            size += 10; // Base overhead per operation
            size += op.Path.Length * 5;
            if (op.Value is not null)
                size += EstimateNodeSize(op.Value);
        }
        return size;
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

    private static object[] CreatePath(object[] basePath, int index)
    {
        var result = new object[basePath.Length + 1];
        Array.Copy(basePath, result, basePath.Length);
        result[basePath.Length] = index;
        return result;
    }
}
