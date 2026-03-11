# Delta Message Transit - Efficient State Synchronization

## Overview

Delta encoding for message payloads - send only what changed, not the entire state. Like git commits: only diffs, not the whole codebase.

---

## Problem

```
Message 1: { "temp": 25.5, "humidity": 60, "device": "sensor-1", "location": "room-a", "battery": 95 }
Message 2: { "temp": 25.6, "humidity": 60, "device": "sensor-1", "location": "room-a", "battery": 95 }
Message 3: { "temp": 25.7, "humidity": 60, "device": "sensor-1", "location": "room-a", "battery": 95 }
```

Only `temp` changed, but 100+ bytes sent each time.

---

## Solution

```
Message 1 (full):  { "temp": 25.5, "humidity": 60, "device": "sensor-1", ... }
Message 2 (delta): [0, ["temp"], 25.6]
Message 3 (delta): [0, ["temp"], 25.7]
```

Receiver reconstructs full state by applying delta to last known state.

---

## Operations

```csharp
enum DeltaOp : byte
{
    Set = 0,           // Set value (add or update)
    Remove = 1,        // Remove property
    SetNull = 2,       // Explicitly set to null
    
    // Array-specific
    ArrayInsert = 11,  // Insert element at index
    ArrayRemove = 12,  // Remove element at index
    ArrayReplace = 14  // Replace entire array (fallback)
}
```

---

## Path Format

**Use array of segments** - no string parsing, no escaping:

```json
// Object: { "a/b/c": 1, "user": { "name": "Alice" }, "items": [10, 20] }

["a/b/c"]              // Property named "a/b/c" (any characters allowed)
["user", "name"]       // Nested: user → name
["items", 0]           // Array index: items[0]
["~weird~", "/slash/"] // Any characters, no escaping needed
```

| String Path (problematic) | Array Path (robust) |
|---------------------------|---------------------|
| `/a~1b~1c` | `["a/b/c"]` |
| `/user/name` | `["user", "name"]` |
| `/items/0` | `["items", 0]` |
| Requires escaping | No escaping |
| String parsing | Type-safe |

---

## Wire Format

```json
// [op, pathSegments[], value?, index?]

// Set property to value
[0, ["name"], "John"]

// Set nested property
[0, ["user", "address", "city"], "Tokyo"]

// Remove property
[1, ["oldField"]]

// Set to null (explicit)
[2, ["middleName"]]

// Array: insert at index
[11, ["items"], 1, "inserted"]     // items.splice(1, 0, "inserted")

// Array: remove at index
[12, ["items"], 2]                 // items.splice(2, 1)

// Array: replace entire (fallback)
[14, ["items"], ["a", "b", "c"]]
```

---

## Complete Example

```
Old state:
{
  "name": "Alice",
  "age": 30,
  "tags": ["a", "b", "c"],
  "address": {
    "city": "NYC",
    "zip": "10001"
  }
}

New state:
{
  "name": "Alice",
  "age": 31,
  "tags": ["a", "x", "b", "c"],
  "address": {
    "city": "NYC",
    "zip": null
  },
  "active": true
}

Delta:
[
  [0, ["age"], 31],                    // age: 30 → 31
  [11, ["tags"], 1, "x"],              // insert "x" at index 1
  [2, ["address", "zip"]],             // zip → null
  [0, ["active"], true]                // add active
]

Wire size: ~80 bytes vs full ~180 bytes = 56% savings
```

---

## Sync Strategy

Compute only what's needed:

| Condition | Action |
|-----------|--------|
| Sender has no local history | Send full (no previous state to diff) |
| Sender has local history | Send delta |
| Receiver has no local history | Request resync → sender sends full |

```csharp
// Sender side
byte[] Encode<T>(T? previousState, T currentState)
{
    if (previousState is null)
        return [0x00, ..Serialize(currentState)];  // Full
    
    var delta = ComputeDelta(previousState, currentState);
    return [0x01, ..Serialize(delta)];  // Delta
}

// Receiver side
(T state, bool needsResync) Decode<T>(byte[] data, T? localState)
{
    if (data[0] == 0x00)  // Full
        return (Deserialize<T>(data[1..]), false);
    
    if (localState is null)  // Delta but no local state
        return (default!, true);  // Request resync
    
    var delta = DeserializeDelta(data[1..]);
    return (ApplyDelta(localState, delta), false);
}
```

---

## Array Diff Algorithm (Smart Matching + Deep Diffing)

Prioritizes **minimal delta size** over speed. Detects deep property changes within array elements.

### Strategy by Array Type

| Array Type | Strategy |
|------------|----------|
| Same-length arrays | Position-based deep diffing (recurses into objects at same index) |
| Different-length object arrays | Smart matching (ID → equality → similarity) + deep diffing |
| Different-length primitive arrays | LCS algorithm for optimal insert/remove |

### Same-Length Arrays

```csharp
List<DeltaOp> DiffArraysSameLength(JsonArray oldArr, JsonArray newArr, object[] path)
{
    var ops = new List<DeltaOp>();
    
    for (int i = 0; i < oldArr.Count; i++)
    {
        var oldElem = oldArr[i];
        var newElem = newArr[i];
        
        if (DeepEquals(oldElem, newElem))
            continue;
        
        // Deep diff: recurse into objects/arrays at same position
        if (oldElem is JsonObject && newElem is JsonObject)
            ops.AddRange(DiffObjects(oldElem, newElem, [..path, i]));
        else if (oldElem is JsonArray && newElem is JsonArray)
            ops.AddRange(DiffArrays(oldElem, newElem, [..path, i]));
        else
            ops.Add(new(Set, [..path, i], newElem));
    }
    
    return ops;
}
```

### Object Arrays with Smart Matching

```csharp
List<DeltaOp> DiffObjectArrays(JsonArray oldArr, JsonArray newArr, object[] path)
{
    var matches = FindBestMatches(oldArr, newArr);
    // matches[newIdx] = oldIdx or -1 if new element
    
    var ops = new List<DeltaOp>();
    var matched = new HashSet<int>();
    
    // Find removals (old elements not matched)
    for (int oi = oldArr.Count - 1; oi >= 0; oi--)
        if (!matched.Contains(oi))
            ops.Add(new(ArrayRemove, path, oi));
    
    // Find insertions and deep diffs
    for (int ni = 0; ni < newArr.Count; ni++)
    {
        if (matches[ni] < 0)
            ops.Add(new(ArrayInsert, path, ni, newArr[ni]));
        else
            ops.AddRange(DiffObjects(oldArr[matches[ni]], newArr[ni], [..path, ni]));
    }
    
    return ops;
}

(int oldIdx)[] FindBestMatches(JsonArray oldArr, JsonArray newArr)
{
    // Pass 1: Match by ID field ("id", "Id", "ID")
    // Pass 2: Match by exact equality
    // Pass 3: Match by structural similarity ≥50%
}
```

### Primitive Arrays (LCS)

```csharp
List<DeltaOp> DiffPrimitiveArrays(JsonArray oldArr, JsonArray newArr, object[] path)
{
    var lcs = ComputeLCS(oldArr, newArr);
    var ops = new List<DeltaOp>();
    
    // Generate ArrayRemove (reverse order) then ArrayInsert
    // LCS determines optimal edit sequence for primitives
    
    if (ops.Count > Math.Max(oldArr.Count, newArr.Count))
        return [new(ArrayReplace, path, newArr)];
    
    return ops;
}
```

---

## Object Diff Algorithm

```csharp
List<DeltaOperation> DiffObjects(JsonNode oldObj, JsonNode newObj, object[] basePath)
{
    var ops = new List<DeltaOperation>();
    
    // Check properties in new state
    foreach (var prop in newObj.AsObject())
    {
        var path = [..basePath, prop.Key];
        var oldValue = oldObj[prop.Key];
        var newValue = prop.Value;
        
        if (oldValue is null)
        {
            // Property added
            ops.Add(new(DeltaOp.Set, path, newValue));
        }
        else if (newValue is null)
        {
            // Property set to null
            ops.Add(new(DeltaOp.SetNull, path));
        }
        else if (!DeepEquals(oldValue, newValue))
        {
            // Property changed - recurse for objects/arrays
            if (newValue is JsonObject && oldValue is JsonObject)
                ops.AddRange(DiffObjects(oldValue, newValue, path));
            else if (newValue is JsonArray newArr && oldValue is JsonArray oldArr)
                ops.AddRange(DiffArrays(oldArr, newArr, path));
            else
                ops.Add(new(DeltaOp.Set, path, newValue));
        }
    }
    
    // Check for removed properties
    foreach (var prop in oldObj.AsObject())
    {
        if (newObj[prop.Key] is null && prop.Value is not null)
            ops.Add(new(DeltaOp.Remove, [..basePath, prop.Key]));
    }
    
    return ops;
}
```

---

## Apply Delta

```csharp
void ApplyDelta(JsonNode root, DeltaOperation[] ops)
{
    foreach (var op in ops)
    {
        var parent = Navigate(root, op.Path[..^1]);
        var lastSegment = op.Path[^1];
        
        switch (op.Op)
        {
            case DeltaOp.Set:
                SetValue(parent, lastSegment, op.Value);
                break;
                
            case DeltaOp.Remove:
                parent.AsObject().Remove((string)lastSegment);
                break;
                
            case DeltaOp.SetNull:
                SetValue(parent, lastSegment, null);
                break;
                
            case DeltaOp.ArrayInsert:
                parent.AsArray().Insert((int)op.Index!, op.Value);
                break;
                
            case DeltaOp.ArrayRemove:
                parent.AsArray().RemoveAt((int)op.Index!);
                break;
                
            case DeltaOp.ArrayReplace:
                SetValue(Navigate(root, op.Path[..^1]), op.Path[^1], op.Value);
                break;
        }
    }
}

JsonNode Navigate(JsonNode root, object[] path)
{
    var current = root;
    foreach (var segment in path)
    {
        current = segment switch
        {
            string prop => current[prop],
            int index => current[index],
            _ => throw new InvalidOperationException()
        };
    }
    return current;
}
```

---

## API Design

### Native AOT Requirement

POCOs require source-generated JSON serialization. Users MUST provide `JsonTypeInfo<T>`:

```csharp
// User defines their POCO
public record SensorReading(double Temp, int Humidity, string Device);

// User provides source-generated context (Native AOT compatible)
[JsonSerializable(typeof(SensorReading))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class AppJsonContext : JsonSerializerContext;
```

### Typed API (POCO)

```csharp
// Sender - pass JsonTypeInfo for Native AOT
var deltaTransit = new DeltaTransit<SensorReading>(channel, AppJsonContext.Default.SensorReading);

await deltaTransit.SendAsync(new SensorReading(25.5, 60, "sensor-1"));
await deltaTransit.SendAsync(new SensorReading(25.6, 60, "sensor-1")); // Only temp delta sent
await deltaTransit.SendAsync(new SensorReading(25.7, 60, "sensor-1")); // Only temp delta sent

// Receiver
var deltaTransit = new DeltaTransit<SensorReading>(channel, AppJsonContext.Default.SensorReading);

await foreach (var reading in deltaTransit.ReceiveAllAsync())
{
    // Always receives fully reconstructed object
    Console.WriteLine($"Temp: {reading.Temp}");
}
```

### Dynamic API (System.Text.Json)

Supports all built-in .NET JSON types for dynamic/untyped scenarios:

```csharp
// JsonObject - mutable object
var deltaTransit = new DeltaTransit<JsonObject>(channel);
await deltaTransit.SendAsync(new JsonObject { ["temp"] = 25.5, ["humidity"] = 60 });
await deltaTransit.SendAsync(new JsonObject { ["temp"] = 25.6, ["humidity"] = 60 });

// JsonNode - any JSON value (object, array, value)
var deltaTransit = new DeltaTransit<JsonNode>(channel);
await deltaTransit.SendAsync(JsonNode.Parse("{\"temp\": 25.5}"));

// JsonArray - for array-focused data
var deltaTransit = new DeltaTransit<JsonArray>(channel);
await deltaTransit.SendAsync(new JsonArray(1, 2, 3));
await deltaTransit.SendAsync(new JsonArray(1, 99, 2, 3)); // Delta: insert at index 1

// JsonDocument - immutable, for read-heavy scenarios
var deltaTransit = new DeltaTransit<JsonDocument>(channel);
await deltaTransit.SendAsync(JsonDocument.Parse("{\"temp\": 25.5}"));
```

### Supported Types

| Type | Namespace | Mutability | Use Case |
|------|-----------|------------|----------|
| `T` (POCO) | User-defined | Immutable* | Strongly-typed data |
| `JsonObject` | System.Text.Json.Nodes | Mutable | Dynamic objects |
| `JsonArray` | System.Text.Json.Nodes | Mutable | Dynamic arrays |
| `JsonNode` | System.Text.Json.Nodes | Mutable | Any JSON value |
| `JsonDocument` | System.Text.Json | Immutable | Read-heavy, pooled |
| `JsonElement` | System.Text.Json | Immutable | Lightweight reads |

### Internal Conversion

```csharp
public class DeltaTransit<T>
{
    private readonly JsonTypeInfo<T>? _typeInfo;
    private JsonNode? _lastSentState;
    private JsonNode? _lastReceivedState;
    
    // For dynamic types (JsonNode, JsonObject, etc.) - no JsonTypeInfo needed
    public DeltaTransit(ConduitChannel channel) : this(channel, null) { }
    
    // For POCOs - JsonTypeInfo required for Native AOT
    public DeltaTransit(ConduitChannel channel, JsonTypeInfo<T>? typeInfo)
    {
        _typeInfo = typeInfo;
        
        // Validate: POCOs must provide JsonTypeInfo
        if (!IsDynamicJsonType(typeof(T)) && typeInfo is null)
            throw new ArgumentNullException(nameof(typeInfo), 
                "JsonTypeInfo required for POCO types. Use source-generated JsonSerializerContext.");
    }
    
    private static bool IsDynamicJsonType(Type t) =>
        t == typeof(JsonNode) || t == typeof(JsonObject) || 
        t == typeof(JsonArray) || t == typeof(JsonDocument) || 
        t == typeof(JsonElement);
    
    public async ValueTask SendAsync(T value)
    {
        // Convert to JsonNode for diffing
        var currentState = ToJsonNode(value);
        
        if (_lastSentState is null)
        {
            // First message: send full
            await SendFullAsync(currentState);
        }
        else
        {
            // Compute and send delta
            var delta = ComputeDelta(_lastSentState, currentState);
            await SendDeltaOrFullAsync(delta, currentState);
        }
        
        _lastSentState = currentState.DeepClone();
    }
    
    private JsonNode ToJsonNode(T value) => value switch
    {
        JsonNode node => node,
        JsonDocument doc => JsonNode.Parse(doc.RootElement.GetRawText())!,
        JsonElement elem => JsonNode.Parse(elem.GetRawText())!,
        _ => JsonSerializer.SerializeToNode(value, _typeInfo!)!  // Uses source-gen
    };
    
    private T FromJsonNode(JsonNode node) => typeof(T) switch
    {
        var t when t == typeof(JsonNode) => (T)(object)node,
        var t when t == typeof(JsonObject) => (T)(object)node.AsObject(),
        var t when t == typeof(JsonArray) => (T)(object)node.AsArray(),
        var t when t == typeof(JsonDocument) => (T)(object)JsonDocument.Parse(node.ToJsonString()),
        _ => node.Deserialize(_typeInfo!)!  // Uses source-gen
    };
}
```

---

## Size Comparison

| Scenario | Full Payload | Delta | Savings |
|----------|--------------|-------|---------|
| 1 field changed in 20-field object | 400 bytes | 25 bytes | 94% |
| Insert 1 item in 100-item array | 2000 bytes | 35 bytes | 98% |
| Change 50% of array | 2000 bytes | 2000 bytes | 0% (fallback) |
| 100 messages, 1 field changes | 20KB | 2.2KB | 89% |

---

## Use Cases

| Scenario | Benefit |
|----------|---------|
| IoT sensors | 90%+ bandwidth savings |
| Game state sync | Only changed positions sent |
| Config sync | One setting changed, 50 fields in config |
| Real-time dashboards | Stock tick, but full metadata |
| Document collaboration | Character changes, not full doc |

---

## Implementation Phases

### Phase 1: Core
- [x] Define DeltaOp enum
- [x] Implement object diff (property-level comparison)
- [x] Implement apply delta
- [x] Adaptive full vs delta selection

**Tests:**
- `DeltaOp_Enum_HasExpectedValues`
- `ObjectDiff_SinglePropertyChanged_ReturnsSetOp`
- `ObjectDiff_PropertyRemoved_ReturnsRemoveOp`
- `ObjectDiff_PropertySetToNull_ReturnsSetNullOp`
- `ObjectDiff_NoChanges_ReturnsEmptyDelta`
- `ObjectDiff_NestedPropertyChanged_ReturnsCorrectPath`
- `ApplyDelta_SetOp_UpdatesProperty`
- `ApplyDelta_RemoveOp_RemovesProperty`
- `ApplyDelta_SetNullOp_SetsPropertyToNull`
- `ApplyDelta_NestedPath_UpdatesNestedProperty`
- `SyncStrategy_NoLocalHistory_SendsFull`
- `SyncStrategy_HasLocalHistory_SendsDelta`
- `SyncStrategy_ReceiverNoLocalHistory_RequestsResync`

### Phase 2: Arrays
- [x] Smart matching for object arrays (ID, equality, similarity)
- [x] Position-based deep diffing for same-length arrays
- [x] LCS algorithm for primitive arrays (internal)
- [x] Array insert/remove/replace operations
- [x] Fallback to full array when ops.Count > max(old, new)

**Tests:**
- `ArrayDiff_InsertAtStart_ReturnsArrayInsertOp`
- `ArrayDiff_InsertAtEnd_ReturnsArrayInsertOp`
- `ArrayDiff_InsertInMiddle_ReturnsArrayInsertOp`
- `ArrayDiff_RemoveFromStart_ReturnsArrayRemoveOp`
- `ArrayDiff_RemoveFromEnd_ReturnsArrayRemoveOp`
- `ArrayDiff_RemoveFromMiddle_ReturnsArrayRemoveOp`
- `ArrayDiff_MajorityChanged_ReturnsArrayReplaceOp`
- `ApplyDelta_ArrayInsertOp_InsertsAtIndex`
- `ApplyDelta_ArrayRemoveOp_RemovesAtIndex`
- `ApplyDelta_ArrayReplaceOp_ReplacesEntireArray`
- `EdgeCase_DeeplyNestedArraysOfObjects_UpdateMiddleItem` (verifies deep diffing)

### Phase 3: Type Support (Native AOT)
- [x] DeltaTransit<T> constructor with JsonTypeInfo<T> parameter
- [x] Validation: POCOs must provide JsonTypeInfo
- [x] DeltaTransit<JsonObject> (no JsonTypeInfo needed)
- [x] DeltaTransit<JsonArray> (no JsonTypeInfo needed)
- [x] DeltaTransit<JsonNode> (no JsonTypeInfo needed)
- [x] DeltaTransit<JsonDocument> (no JsonTypeInfo needed)
- [x] DeltaTransit<JsonElement> (no JsonTypeInfo needed)
- [x] Internal ToJsonNode/FromJsonNode using JsonTypeInfo

**Tests:**
- `DeltaTransit_POCO_WithJsonTypeInfo_Succeeds`
- `DeltaTransit_POCO_WithoutJsonTypeInfo_ThrowsArgumentNullException`
- `DeltaTransit_JsonObject_WithoutJsonTypeInfo_Succeeds`
- `DeltaTransit_JsonArray_WithoutJsonTypeInfo_Succeeds`
- `DeltaTransit_JsonNode_WithoutJsonTypeInfo_Succeeds`
- `DeltaTransit_JsonDocument_WithoutJsonTypeInfo_Succeeds`
- `DeltaTransit_JsonElement_WithoutJsonTypeInfo_Succeeds`
- `ToJsonNode_POCO_UsesSourceGeneratedSerializer`
- `ToJsonNode_JsonObject_ReturnsDirectly`
- `ToJsonNode_JsonDocument_ParsesRootElement`
- `FromJsonNode_POCO_UsesSourceGeneratedDeserializer`
- `FromJsonNode_JsonObject_ReturnsAsObject`
- `FromJsonNode_JsonArray_ReturnsAsArray`

### Phase 4: Integration
- [x] Integration with existing MessageTransit
- [x] Serialization (JSON wire format)
- [x] State management (last sent/received)

**Tests:**
- `DeltaTransit_SendAsync_FirstMessage_SendsFull`
- `DeltaTransit_SendAsync_SecondMessage_SendsDelta`
- `DeltaTransit_ReceiveAsync_FullMessage_ReconstructsState`
- `DeltaTransit_ReceiveAsync_DeltaMessage_AppliesAndReconstructsState`
- `DeltaTransit_ReceiveAsync_DeltaWithoutLocalState_RequestsResync`
- `DeltaTransit_StateManagement_TracksLastSentState`
- `DeltaTransit_StateManagement_TracksLastReceivedState`
- `WireFormat_Serialize_ProducesValidJson`
- `WireFormat_Deserialize_ParsesValidJson`
- `WireFormat_RoundTrip_PreservesAllOperations`

### Phase 5: Optimization
- [x] Binary encoding for operations
- [x] Path compression for repeated paths
- [x] Benchmarks vs full payloads

**Tests:**
- `BinaryEncoding_Serialize_SmallerThanJson`
- `BinaryEncoding_Deserialize_ParsesCorrectly`
- `BinaryEncoding_RoundTrip_PreservesAllOperations`
- `PathCompression_RepeatedPaths_CompressesSuccessfully`
- `PathCompression_DecompressedPaths_MatchOriginal`

---

## Design Decisions

| Decision | Choice | Reason |
|----------|--------|--------|
| Path format | Array of segments | No escaping, type-safe, robust |
| Null handling | Separate SetNull op | Distinguish null value vs removed |
| Array diff | Smart matching + deep diffing | Minimal delta size (prioritized over speed) |
| Same-length arrays | Position-based | Elements at same index compared recursively |
| Object arrays | ID/equality/similarity matching | Find corresponding elements across different lengths |
| Primitive arrays | LCS-based | Optimal insert/remove sequence |
| Fallback | Full payload | When ops.Count > max(old, new) |
| Wire format | JSON array | Compact, debuggable |
| Type support | All System.Text.Json types | Native .NET, no dependencies |
| Internal repr | JsonNode | Mutable, easy to diff/apply |
| Native AOT | Require JsonTypeInfo<T> | No reflection, trim-safe |
| Dynamic types | No JsonTypeInfo needed | JsonNode/JsonObject already AOT-safe |
