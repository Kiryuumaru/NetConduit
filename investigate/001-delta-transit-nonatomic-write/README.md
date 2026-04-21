# BUG: DeltaTransit Non-Atomic Write — Message Framing Corruption

## Severity: HIGH

## Summary

`DeltaTransit.WriteMessageAsync()` writes the 4-byte length prefix and the message body
as **two separate `WriteAsync` calls** to the underlying `WriteChannel`. This violates
message framing atomicity. If the transport fails between the two writes, a partial
message (length prefix only) is buffered, permanently corrupting the receiver's framing
state after reconnection.

## Evidence

### Code Location

[src/NetConduit/Transits/DeltaTransit.cs](../../src/NetConduit/Transits/DeltaTransit.cs) —
`WriteMessageAsync` method:

```csharp
private async ValueTask WriteMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
{
    if (_writeChannel is null) return;

    // BUG: Two separate writes — NOT atomic
    Span<byte> lengthPrefix = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, data.Length);

    await _writeChannel.WriteAsync(lengthPrefix.ToArray(), cancellationToken);  // Write 1
    await _writeChannel.WriteAsync(data, cancellationToken);                     // Write 2
}
```

### Contrast with MessageTransit (correct)

[src/NetConduit/Transits/MessageTransit.cs](../../src/NetConduit/Transits/MessageTransit.cs) —
`SendAsync` method correctly combines into one write:

```csharp
var combinedBuffer = ArrayPool<byte>.Shared.Rent(totalLength);
BinaryPrimitives.WriteUInt32BigEndian(combinedBuffer, (uint)jsonBytes.Length);
jsonBytes.CopyTo(combinedBuffer, 4);
await _writeChannel.WriteAsync(combinedBuffer.AsMemory(0, totalLength), cancellationToken);  // Single write
```

## Attack Scenario

1. Sender calls `DeltaTransit.SendAsync(state)`
2. First `WriteAsync` (4 bytes length prefix) succeeds and is buffered in multiplexer
3. Transport disconnects before second `WriteAsync` completes
4. On reconnection, the 4-byte length prefix is delivered to receiver
5. Receiver's `ReadMessageAsync` reads those 4 bytes as a length prefix, e.g. `{length: 256}`
6. Receiver then reads the next 256 bytes — which are the START of the next message's length prefix + body
7. **All subsequent messages are frame-shifted and corrupted forever**

## CWE Reference

- [CWE-354: Improper Validation of Integrity Check Value](https://cwe.mitre.org/data/definitions/354.html)
- [CWE-20: Improper Input Validation](https://cwe.mitre.org/data/definitions/20.html)

## Recommended Fix

Combine the length prefix and body into a single `WriteAsync` call, matching the
pattern already used in `MessageTransit.SendAsync`:

```csharp
private async ValueTask WriteMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
{
    if (_writeChannel is null) return;

    var buffer = ArrayPool<byte>.Shared.Rent(4 + data.Length);
    try
    {
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        data.CopyTo(buffer.AsMemory(4));
        await _writeChannel.WriteAsync(buffer.AsMemory(0, 4 + data.Length), cancellationToken);
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
```

Key points:
- Single `WriteAsync` ensures the length prefix and body are atomically buffered
- `ArrayPool<byte>` avoids per-message allocation overhead
- If the write fails, either 0 bytes or the entire message is buffered — no partial state

## Reproduction

See `DeltaTransitNonAtomicWriteTest.cs` — test demonstrates the two-write pattern
by inspecting channel write counts.
