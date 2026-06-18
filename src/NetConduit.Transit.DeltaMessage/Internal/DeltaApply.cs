using System.Text.Json.Nodes;
using NetConduit.Enums;
using NetConduit.Models;

namespace NetConduit.Transit.DeltaMessage.Internal;

internal static class DeltaApply
{
    public static void ApplyDelta(JsonNode root, IReadOnlyList<DeltaOperation> ops)
    {
        foreach (var op in ops)
        {
            ApplyOperation(root, op);
        }
    }

    internal static void ApplyOperation(JsonNode root, DeltaOperation op)
    {
        if (op.Path.Length == 0)
        {
            ApplyRootOperation(root, op);
            return;
        }

        switch (op.Op)
        {
            case DeltaOp.Set:
            {
                var parent = Navigate(root, op.Path[..^1]);
                var lastSegment = op.Path[^1];
                SetValue(parent, lastSegment, op.Value?.DeepClone());
                break;
            }

            case DeltaOp.Remove:
            {
                var parent = Navigate(root, op.Path[..^1]);
                var lastSegment = op.Path[^1];
                if (parent is JsonObject obj && lastSegment is string key)
                {
                    obj.Remove(key);
                }
                else
                {
                    throw new InvalidOperationException($"Remove operation requires object parent and string key. Got {parent?.GetType().Name} and {lastSegment?.GetType().Name}");
                }
                break;
            }

            case DeltaOp.SetNull:
            {
                var parent = Navigate(root, op.Path[..^1]);
                var lastSegment = op.Path[^1];
                SetValue(parent, lastSegment, null);
                break;
            }

            case DeltaOp.ArrayInsert:
            {
                var array = Navigate(root, op.Path);
                if (array is JsonArray arr && op.Index is int idx)
                {
                    if ((uint)idx > (uint)arr.Count)
                    {
                        throw new InvalidOperationException(
                            $"ArrayInsert index {idx} is out of range for array of length {arr.Count}.");
                    }
                    arr.Insert(idx, op.Value?.DeepClone());
                }
                else
                {
                    throw new InvalidOperationException($"ArrayInsert requires array target and index. Got {array?.GetType().Name}");
                }
                break;
            }

            case DeltaOp.ArrayRemove:
            {
                var array = Navigate(root, op.Path);
                if (array is JsonArray arr && op.Index is int idx)
                {
                    if ((uint)idx >= (uint)arr.Count)
                    {
                        throw new InvalidOperationException(
                            $"ArrayRemove index {idx} is out of range for array of length {arr.Count}.");
                    }
                    arr.RemoveAt(idx);
                }
                else
                {
                    throw new InvalidOperationException($"ArrayRemove requires array target and index. Got {array?.GetType().Name}");
                }
                break;
            }

            case DeltaOp.ArrayReplace:
            {
                var parent = Navigate(root, op.Path[..^1]);
                var lastSegment = op.Path[^1];
                SetValue(parent, lastSegment, op.Value?.DeepClone());
                break;
            }

            default:
                throw new InvalidOperationException($"Unknown delta operation: {op.Op}");
        }
    }

    private static void ApplyRootOperation(JsonNode root, DeltaOperation op)
    {
        switch (op.Op)
        {
            case DeltaOp.ArrayInsert:
            {
                if (root is JsonArray arr && op.Index is int idx)
                {
                    if ((uint)idx > (uint)arr.Count)
                    {
                        throw new InvalidOperationException(
                            $"Root ArrayInsert index {idx} is out of range for array of length {arr.Count}.");
                    }
                    arr.Insert(idx, op.Value?.DeepClone());
                    break;
                }

                throw new InvalidOperationException($"Root ArrayInsert requires array root and index. Got {root.GetType().Name}");
            }

            case DeltaOp.ArrayRemove:
            {
                if (root is JsonArray arr && op.Index is int idx)
                {
                    if ((uint)idx >= (uint)arr.Count)
                    {
                        throw new InvalidOperationException(
                            $"Root ArrayRemove index {idx} is out of range for array of length {arr.Count}.");
                    }
                    arr.RemoveAt(idx);
                    break;
                }

                throw new InvalidOperationException($"Root ArrayRemove requires array root and index. Got {root.GetType().Name}");
            }

            case DeltaOp.ArrayReplace:
            {
                if (root is JsonArray arr && op.Value is JsonArray replacement)
                {
                    for (var i = arr.Count - 1; i >= 0; i--)
                    {
                        arr.RemoveAt(i);
                    }

                    foreach (var item in replacement)
                    {
                        arr.Add(item?.DeepClone());
                    }
                    break;
                }

                throw new InvalidOperationException($"Root ArrayReplace requires array root and array value. Got {root.GetType().Name}");
            }

            default:
                throw new InvalidOperationException("Cannot apply root replacement delta in place. Use full state replacement instead.");
        }
    }

    internal static JsonNode Navigate(JsonNode root, ReadOnlySpan<object> path)
    {
        JsonNode? current = root;

        foreach (var segment in path)
        {
            current = segment switch
            {
                string prop => current?[prop],
                int index => current?[index],
                _ => throw new InvalidOperationException($"Invalid path segment type: {segment?.GetType().Name}")
            };

            if (current is null)
            {
                throw new InvalidOperationException($"Path segment '{segment}' not found during navigation.");
            }
        }

        return current;
    }

    private static void SetValue(JsonNode parent, object segment, JsonNode? value)
    {
        switch (parent)
        {
            case JsonObject obj when segment is string key:
                obj[key] = value;
                break;

            case JsonArray arr when segment is int index:
                arr[index] = value;
                break;

            default:
                throw new InvalidOperationException($"Cannot set value: incompatible parent type {parent?.GetType().Name} with segment type {segment?.GetType().Name}");
        }
    }
}
