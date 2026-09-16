using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetConduit.Transit;

// Shared receive-side JSON hardening for the transit packages (Message and
// DeltaMessage). Lives outside the core multiplexer package on purpose: the
// limits only constrain peer-supplied payloads at transit receive boundaries,
// never framing, transport, or channel behavior.
//
// Two backstops, in order:
//   1. Framing cap (maxMessageSize, default 16 MiB) runs first at the
//      length-prefix layer and rejects over-cap frames before any parsing.
//   2. This helper runs second at the JSON-parse layer: a depth cap and a
//      token-count cap that reject hostile shapes (deep nesting, giant
//      arrays/objects) that fit inside the byte cap.
// All rejections throw System.Text.Json.JsonException (never a new public
// exception type) so callers handle one familiar parse-failure contract.
internal static class TransitJsonLimits
{
    // Matches the framework default (JsonDocumentOptions/reader MaxDepth = 64).
    // Down-only: callers may tighten, never loosen.
    public const int DefaultMaxDepth = 64;

    // Token backstop: total JSON nodes + scalar values counted during the
    // hardened walk. Sized so legitimate 16 MiB payloads pass comfortably
    // while hostile million-element arrays are rejected before allocation
    // pressure grows.
    public const int DefaultMaxTokenCount = 1_000_000;

    public static JsonDocumentOptions DocumentOptions(int maxDepth = DefaultMaxDepth)
    {
        if (maxDepth <= 0 || maxDepth > DefaultMaxDepth)
            throw new ArgumentOutOfRangeException(nameof(maxDepth),
                $"MaxDepth must be between 1 and {DefaultMaxDepth} (down-only).");
        return new JsonDocumentOptions { MaxDepth = maxDepth };
    }

    public static JsonSerializerOptions ClampSerializerOptions(JsonSerializerOptions? options, int maxDepth = DefaultMaxDepth)
    {
        if (maxDepth <= 0 || maxDepth > DefaultMaxDepth)
            throw new ArgumentOutOfRangeException(nameof(maxDepth),
                $"MaxDepth must be between 1 and {DefaultMaxDepth} (down-only).");
        // Never mutate the caller's instance: clone, then clamp down-only.
        // The copy constructor preserves the caller's converters and settings.
        var effective = options is null ? new JsonSerializerOptions() : new JsonSerializerOptions(options);
        if (effective.MaxDepth > maxDepth)
            effective.MaxDepth = maxDepth;
        return effective;
    }

    public static void ThrowIfTypeInfoDepthExceeded<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>? typeInfo, int maxDepth = DefaultMaxDepth)
    {
        if (maxDepth <= 0 || maxDepth > DefaultMaxDepth)
            throw new ArgumentOutOfRangeException(nameof(maxDepth),
                $"MaxDepth must be between 1 and {DefaultMaxDepth} (down-only).");
        // JsonTypeInfo is source-generated and immutable: it cannot be
        // clamped per call, so a caller configuration deeper than the
        // transit backstop is rejected loudly instead of silently widened.
        var configured = typeInfo?.Options?.MaxDepth ?? DefaultMaxDepth;
        if (configured > maxDepth)
            throw new JsonException(
                $"Receive type depth limit {configured} exceeds the transit backstop of {maxDepth}. Tighten the configured MaxDepth instead of widening the transit.");
    }
}

internal static class JsonHardening
{
    // Depth is enforced by the document options (JsonNode.Parse has no
    // depth knob of its own); the token budget is enforced by the walk below.
    public static JsonNode? ParseNode(
        ReadOnlySpan<byte> json,
        int maxDepth = TransitJsonLimits.DefaultMaxDepth,
        int maxTokenCount = TransitJsonLimits.DefaultMaxTokenCount)
    {
        var node = JsonNode.Parse(json, null, TransitJsonLimits.DocumentOptions(maxDepth));
        ThrowIfTokenBudgetExceeded(node, maxTokenCount);
        return node;
    }

    // Depth + token gate for paths that deserialize straight to T without
    // materializing a JsonNode. The caller deserializes from the returned
    // document's RootElement so the payload text is parsed once.
    // Takes ReadOnlyMemory<byte> because JsonDocument.Parse has no span overload.
    public static JsonDocument GateDocument(
        ReadOnlyMemory<byte> json,
        int maxDepth = TransitJsonLimits.DefaultMaxDepth,
        int maxTokenCount = TransitJsonLimits.DefaultMaxTokenCount)
    {
        var doc = JsonDocument.Parse(json, TransitJsonLimits.DocumentOptions(maxDepth));
        try
        {
            ThrowIfDocumentBudgetExceeded(doc, maxTokenCount);
            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    public static void ThrowIfTokenBudgetExceeded(JsonNode? node, int maxTokenCount)
    {
        if (maxTokenCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTokenCount), "MaxTokenCount must be positive.");
        long count = 0;
        CountNode(node, ref count, maxTokenCount);
    }

    public static void ThrowIfDocumentBudgetExceeded(JsonDocument doc, int maxTokenCount)
    {
        if (maxTokenCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTokenCount), "MaxTokenCount must be positive.");
        long count = 0;
        CountElement(doc.RootElement, ref count, maxTokenCount);
    }

    private static void CountNode(JsonNode? node, ref long count, int maxTokenCount)
    {
        count++;
        if (count > maxTokenCount)
            throw new JsonException($"JSON payload exceeds token budget of {maxTokenCount} tokens.");
        switch (node)
        {
            case JsonObject obj:
                foreach (var prop in obj)
                    CountNode(prop.Value, ref count, maxTokenCount);
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    CountNode(item, ref count, maxTokenCount);
                break;
        }
    }

    private static void CountElement(JsonElement element, ref long count, int maxTokenCount)
    {
        count++;
        if (count > maxTokenCount)
            throw new JsonException($"JSON payload exceeds token budget of {maxTokenCount} tokens.");
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    CountElement(prop.Value, ref count, maxTokenCount);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CountElement(item, ref count, maxTokenCount);
                break;
        }
    }
}
