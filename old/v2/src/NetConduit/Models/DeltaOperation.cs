using System.Text.Json.Nodes;
using NetConduit.Enums;

namespace NetConduit.Models;

/// <summary>
/// Represents a single delta operation to apply to a JSON state.
/// </summary>
/// <param name="Op">The operation type.</param>
/// <param name="Path">Path segments to the target location (strings for properties, ints for array indices).</param>
/// <param name="Value">The value for Set, SetNull, ArrayInsert, ArrayReplace operations.</param>
/// <param name="Index">The array index for ArrayInsert, ArrayRemove operations.</param>
public readonly record struct DeltaOperation(
    DeltaOp Op,
    object[] Path,
    JsonNode? Value = null,
    int? Index = null);
