using System.Text.Json.Nodes;
using NetConduit.Enums;
using NetConduit.Models;

namespace NetConduit.Transit.DeltaMessage.Internal;

internal static class DeltaDiff
{
    public static List<DeltaOperation> ComputeDelta(JsonNode? oldState, JsonNode? newState)
    {
        if (oldState is null || newState is null)
            return [];

        if (oldState is JsonObject oldObj && newState is JsonObject newObj)
            return DiffObjects(oldObj, newObj, []);

        if (oldState is JsonArray oldArr && newState is JsonArray newArr)
            return DiffArrays(oldArr, newArr, []);

        if (!DeepEquals(oldState, newState))
            return [new DeltaOperation(DeltaOp.Set, [], newState?.DeepClone())];

        return [];
    }

    internal static List<DeltaOperation> DiffObjects(JsonObject oldObj, JsonObject newObj, object[] basePath)
    {
        var ops = new List<DeltaOperation>();

        foreach (var prop in newObj)
        {
            var path = CreatePath(basePath, prop.Key);
            var newValue = prop.Value;

            // ContainsKey distinguishes "key absent" from "key present with
            // JSON null value" — JsonObject's indexer returns C# null for
            // both, which silently dropped newly-added null-valued
            // properties from the diff (issue #211).
            if (!oldObj.ContainsKey(prop.Key))
            {
                ops.Add(new DeltaOperation(DeltaOp.Set, path, newValue?.DeepClone()));
                continue;
            }

            var oldValue = oldObj[prop.Key];

            if (oldValue is null && newValue is not null)
            {
                ops.Add(new DeltaOperation(DeltaOp.Set, path, newValue.DeepClone()));
            }
            else if (newValue is null && oldValue is not null)
            {
                ops.Add(new DeltaOperation(DeltaOp.SetNull, path));
            }
            else if (oldValue is not null && newValue is not null && !DeepEquals(oldValue, newValue))
            {
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
                    ops.Add(new DeltaOperation(DeltaOp.Set, path, newValue.DeepClone()));
                }
            }
        }

        foreach (var prop in oldObj)
        {
            if (!newObj.ContainsKey(prop.Key))
            {
                ops.Add(new DeltaOperation(DeltaOp.Remove, CreatePath(basePath, prop.Key)));
            }
        }

        return ops;
    }

    internal static List<DeltaOperation> DiffArrays(JsonArray oldArr, JsonArray newArr, object[] basePath)
    {
        if (oldArr.Count == newArr.Count)
        {
            return DiffArraysSameLength(oldArr, newArr, basePath);
        }

        return DiffArraysDifferentLength(oldArr, newArr, basePath);
    }

    private static List<DeltaOperation> DiffArraysSameLength(JsonArray oldArr, JsonArray newArr, object[] basePath)
    {
        var ops = new List<DeltaOperation>();

        for (int i = 0; i < oldArr.Count; i++)
        {
            var oldElem = oldArr[i];
            var newElem = newArr[i];

            if (DeepEquals(oldElem, newElem))
                continue;

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

    private static List<DeltaOperation> DiffArraysDifferentLength(JsonArray oldArr, JsonArray newArr, object[] basePath)
    {
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

    private static List<DeltaOperation> DiffObjectArrays(JsonArray oldArr, JsonArray newArr, object[] basePath)
    {
        var ops = new List<DeltaOperation>();

        var (matchedPairs, unmatchedOld, unmatchedNew) = FindBestMatches(oldArr, newArr);

        if (RequiresReorderFallback(matchedPairs))
        {
            return [new DeltaOperation(DeltaOp.ArrayReplace, basePath, newArr.DeepClone())];
        }

        var sortedRemovals = unmatchedOld.OrderByDescending(i => i).ToList();
        foreach (var oldIdx in sortedRemovals)
        {
            ops.Add(new DeltaOperation(DeltaOp.ArrayRemove, basePath, Index: oldIdx));
        }

        foreach (var newIdx in unmatchedNew.OrderBy(i => i))
        {
            ops.Add(new DeltaOperation(DeltaOp.ArrayInsert, basePath, newArr[newIdx]?.DeepClone(), newIdx));
        }

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

        if (ops.Count > Math.Max(oldArr.Count, newArr.Count))
        {
            return [new DeltaOperation(DeltaOp.ArrayReplace, basePath, newArr.DeepClone())];
        }

        return ops;
    }

    private static bool RequiresReorderFallback(List<(int oldIdx, int newIdx)> matchedPairs)
    {
        var previousOldIndex = -1;

        foreach (var (oldIdx, _) in matchedPairs.OrderBy(pair => pair.newIdx))
        {
            if (oldIdx < previousOldIndex)
            {
                return true;
            }

            previousOldIndex = oldIdx;
        }

        return false;
    }

    private static List<DeltaOperation> DiffPrimitiveArrays(JsonArray oldArr, JsonArray newArr, object[] basePath)
    {
        const long maxLcsProduct = 1_000_000;
        if ((long)oldArr.Count * newArr.Count > maxLcsProduct)
        {
            return [new DeltaOperation(DeltaOp.ArrayReplace, basePath, newArr.DeepClone())];
        }

        var ops = new List<DeltaOperation>();
        var lcs = ComputeLCS(oldArr, newArr);

        int oldIdx = 0, newIdx = 0, lcsIdx = 0;

        while (oldIdx < oldArr.Count || newIdx < newArr.Count)
        {
            bool oldMatchesLcs = lcsIdx < lcs.Count && oldIdx < oldArr.Count && DeepEquals(oldArr[oldIdx], lcs[lcsIdx]);
            bool newMatchesLcs = lcsIdx < lcs.Count && newIdx < newArr.Count && DeepEquals(newArr[newIdx], lcs[lcsIdx]);

            if (oldMatchesLcs && newMatchesLcs)
            {
                oldIdx++;
                newIdx++;
                lcsIdx++;
            }
            else if (newIdx < newArr.Count && !newMatchesLcs)
            {
                ops.Add(new DeltaOperation(DeltaOp.ArrayInsert, basePath, newArr[newIdx]?.DeepClone(), newIdx));
                newIdx++;
            }
            else if (oldIdx < oldArr.Count && !oldMatchesLcs)
            {
                ops.Add(new DeltaOperation(DeltaOp.ArrayRemove, basePath, Index: newIdx));
                oldIdx++;
            }
        }

        if (ops.Count > Math.Max(oldArr.Count, newArr.Count))
        {
            return [new DeltaOperation(DeltaOp.ArrayReplace, basePath, newArr.DeepClone())];
        }

        return ops;
    }

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

    private static (List<(int oldIdx, int newIdx)> matched, List<int> unmatchedOld, List<int> unmatchedNew)
        FindBestMatches(JsonArray oldArr, JsonArray newArr)
    {
        var matched = new List<(int oldIdx, int newIdx)>();
        var usedOld = new HashSet<int>();
        var usedNew = new HashSet<int>();

        // Pass 1: Match by "id" field
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

        // Pass 2: Match by exact equality
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

        // Pass 3: Match by structural similarity
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
                if (similarity > bestSimilarity && similarity >= 0.5)
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

    private static string? GetIdValue(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;

        if (obj.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue idVal)
        {
            return idVal.ToString();
        }

        return null;
    }

    private static double ComputeSimilarity(JsonObject a, JsonObject b)
    {
        var keysA = a.Select(p => p.Key).ToHashSet();
        var keysB = b.Select(p => p.Key).ToHashSet();

        var intersection = keysA.Intersect(keysB).Count();
        var union = keysA.Union(keysB).Count();

        if (union == 0) return 1.0;

        var keySimilarity = (double)intersection / union;

        int matchingValues = 0;
        foreach (var key in keysA.Intersect(keysB))
        {
            if (DeepEquals(a[key], b[key]))
                matchingValues++;
        }

        var valueSimilarity = intersection > 0 ? (double)matchingValues / intersection : 0;

        return keySimilarity * 0.6 + valueSimilarity * 0.4;
    }

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
