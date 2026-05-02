using System.Text.Json.Nodes;
using NetConduit.Enums;
using NetConduit.Models;

namespace NetConduit.Internal;

/// <summary>
/// Applies delta operations to a JSON state.
/// </summary>
internal static class DeltaApply
{
    /// <summary>
    /// Applies delta operations to a JSON node, mutating it in place.
    /// </summary>
    public static void ApplyDelta(JsonNode root, IReadOnlyList<DeltaOperation> ops)
    {
        foreach (var op in ops)
        {
            ApplyOperation(root, op);
        }
    }

    /// <summary>
    /// Applies a single delta operation to a JSON node.
    /// </summary>
    internal static void ApplyOperation(JsonNode root, DeltaOperation op)
    {
        // Handle root-level operations (empty path)
        if (op.Path.Length == 0)
        {
            // Cannot replace root in-place; this case should be handled by the caller
            throw new InvalidOperationException("Cannot apply operation to root node with empty path. Use full state replacement instead.");
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
                // For array operations, path points to the array itself
                var array = Navigate(root, op.Path);
                if (array is JsonArray arr && op.Index is int idx)
                {
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
                // For array operations, path points to the array itself
                var array = Navigate(root, op.Path);
                if (array is JsonArray arr && op.Index is int idx)
                {
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

    /// <summary>
    /// Navigates to a node at the given path.
    /// </summary>
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

    /// <summary>
    /// Sets a value at the given location in a parent node.
    /// </summary>
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
