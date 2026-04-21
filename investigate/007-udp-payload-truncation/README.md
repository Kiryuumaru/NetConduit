# BUG: UDP Payload Length Truncated to UInt16 — Silent Data Loss on Large MTU

## Severity: MEDIUM

## Summary

`ReliableUdpStream.SendPacketAsync()` encodes the payload length as a 2-byte `ushort`
in the packet header. If an MTU value larger than 65542 is configured (making the max
payload exceed 65535 bytes), the length field silently truncates, causing the receiver
to read fewer bytes than were actually sent — a **silent data truncation** bug.

## Evidence

### Code Location

[src/NetConduit.Udp/ReliableUdpStream.cs](../../src/NetConduit.Udp/ReliableUdpStream.cs):

```csharp
// SendPacketAsync — length encoded as UInt16
BinaryPrimitives.WriteUInt16BigEndian(header[5..7], (ushort)payload.Length);
```

```csharp
// WriteAsync — max payload calculation
var maxPayload = Math.Max(1, _options.Mtu - 7); // 7-byte header
```

```csharp
// ReceiveLoopAsync — length read as UInt16
var len = BinaryPrimitives.ReadUInt16BigEndian(span[5..7]);
var payload = span.Slice(7, Math.Min(len, span.Length - 7)).ToArray();
```

### The Bug

1. `ReliableUdpOptions.Mtu` has **no upper bound validation**
2. If `Mtu = 70000`, then `maxPayload = 69993`
3. Sender writes `payload.Length = 69993` → `(ushort)69993 = 4457` (truncated!)
4. Receiver reads `len = 4457`, copies only 4457 of 69993 bytes
5. **65,536 bytes silently lost per packet**

### Also: No MTU Validation

[src/NetConduit.Udp/ReliableUdpOptions.cs](../../src/NetConduit.Udp/ReliableUdpOptions.cs):
```csharp
public int Mtu { get; init; } = 1400;
// No validation: 0, negative, or > 65542 all accepted
```

## CWE Reference

- [CWE-681: Incorrect Conversion between Numeric Types](https://cwe.mitre.org/data/definitions/681.html)
- [CWE-197: Numeric Truncation Error](https://cwe.mitre.org/data/definitions/197.html)
- [CWE-20: Improper Input Validation](https://cwe.mitre.org/data/definitions/20.html)

## Recommended Fix

Two complementary fixes — validate the MTU range AND guard the cast:

**1. Validate MTU in `ReliableUdpOptions`:**

```csharp
public class ReliableUdpOptions
{
    public const int HeaderSize = 7;
    public const int MinMtu = HeaderSize + 1;       // 8 bytes minimum
    public const int MaxMtu = ushort.MaxValue + HeaderSize; // 65542 — max for ushort payload

    private int _mtu = 1400;

    public int Mtu
    {
        get => _mtu;
        init
        {
            if (value < MinMtu || value > MaxMtu)
                throw new ArgumentOutOfRangeException(nameof(Mtu),
                    $"Must be between {MinMtu} and {MaxMtu}.");
            _mtu = value;
        }
    }
}
```

**2. Add a checked cast as defense-in-depth:**

```csharp
// In SendPacketAsync:
if (payload.Length > ushort.MaxValue)
    throw new InvalidOperationException(
        $"Payload length {payload.Length} exceeds ushort encoding limit.");

BinaryPrimitives.WriteUInt16BigEndian(header[5..7], (ushort)payload.Length);
```

Key points:
- The MTU clamp ensures `maxPayload = Mtu - 7` never exceeds `ushort.MaxValue` (65535)
- The checked cast in `SendPacketAsync` is defense-in-depth — it catches bugs even if
  validation is bypassed or the calculation changes in the future
- Both fixes together make the truncation impossible through two independent barriers

## Reproduction

See `UdpPayloadTruncationTest.cs` — proves the ushort truncation math.
