# AOT and source generators

NetConduit is AOT- and trim-safe end-to-end. The core uses no reflection. The transits that handle JSON expose AOT-friendly overloads.

## Core library

`NetConduit.csproj` sets `IsAotCompatible=true` and `EnableTrimAnalyzer=true`. No `[RequiresUnreferencedCode]` or `[RequiresDynamicCode]` attributes are required to use the core API.

## Message and DeltaMessage transits

Both transits accept a source-generated `JsonTypeInfo<T>` from a `JsonSerializerContext`. This is the AOT path:

```csharp
using System.Text.Json.Serialization;

public record Message(string From, string Text);

[JsonSerializable(typeof(Message))]
internal partial class AppJson : JsonSerializerContext;
```

Use the generated context with the transit:

```csharp
await using var t = await mux.OpenMessageTransitAsync(
    "chat",
    AppJson.Default.Message);
```

`AppJson.Default.Message` is a `JsonTypeInfo<Message>`. The transit serializes and deserializes via it — no reflection, trim-safe.

## Non-AOT overloads

For convenience, both transits expose overloads that take a plain `JsonSerializerOptions`:

```csharp
[RequiresUnreferencedCode("...")]
[RequiresDynamicCode("...")]
public MessageTransit(IWriteChannel? write, IReadChannel? read,
                      JsonSerializerOptions? jsonOptions = null,
                      int maxMessageSize = 16 * 1024 * 1024);
```

The trim analyzer will flag callers. Prefer the `JsonTypeInfo<T>` overloads for production AOT builds.

## Dynamic JSON (`JsonNode`, `JsonObject`)

`DeltaMessageTransit<T>` has overloads that take no `JsonTypeInfo` and work on dynamic JSON types (`JsonNode`, `JsonObject`, `JsonArray`, `JsonDocument`, `JsonElement`). This path uses `System.Text.Json`'s dynamic JSON APIs, which are AOT-safe — no source generator needed.

```csharp
await using var t = await mux.OpenDeltaMessageTransitAsync<JsonObject>("state");
```

## Recommendations

| Scenario | Use |
| --- | --- |
| Strongly typed records / DTOs | `JsonTypeInfo<T>` from a source-gen context. |
| Schemaless or dynamic shapes | `JsonNode` / `JsonObject` with the dynamic constructor. |
| Quick prototypes, no AOT goal | `JsonSerializerOptions` overload. |

## Source generator tips

- Mark the context `partial` and `internal` (or `public` if it crosses assembly boundaries).
- Add `[JsonSerializable(typeof(X))]` for every distinct top-level message type. The generator follows referenced types automatically.
- Use `[JsonSourceGenerationOptions(...)]` to set naming policy, default values handling, etc.

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GameState))]
[JsonSerializable(typeof(InputEvent))]
internal partial class AppJson : JsonSerializerContext;
```
