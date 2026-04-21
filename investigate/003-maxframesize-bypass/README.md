# BUG: No MaxFrameSize Validation — Negative/Zero Values Break Protocol or Allow DoS

## Severity: HIGH (Security)

## Summary

`MultiplexerOptions.MaxFrameSize` is an `int` with **no validation**. Setting it to
a negative or zero value causes the multiplexer to reject ALL frames (including the
handshake), breaking the protocol entirely. Setting it to `int.MaxValue` (2GB) allows
a malicious peer to trigger massive memory allocations.

## Evidence

### Code Location

[src/NetConduit/Models/MultiplexerOptions.cs](../../src/NetConduit/Models/MultiplexerOptions.cs):
```csharp
public int MaxFrameSize { get; init; } = 16 * 1024 * 1024;
// No validation — any int value accepted
```

[src/NetConduit/StreamMultiplexer.cs](../../src/NetConduit/StreamMultiplexer.cs) — `TryParseFrame`:
```csharp
if (header.Length > _options.MaxFrameSize)
{
    throw new MultiplexerException(ErrorCode.ProtocolError,
        $"Frame size {header.Length} exceeds maximum {_options.MaxFrameSize}");
}
```

### The Promotion Behavior (C#-specific)

In C#, `uint > int` promotes **both** to `long` (not uint like C/C++):
- When `MaxFrameSize = -1`: `(long)header.Length > (long)(-1)` → **always true**
- This means ALL frames are rejected, including the handshake frame
- The multiplexer hangs forever, unable to complete connection

### DoS via Large MaxFrameSize

When `MaxFrameSize = int.MaxValue` (2,147,483,647):
- A malicious peer can claim a ~2GB frame payload
- The multiplexer allocates 2GB to read the frame
- Memory exhaustion / OOM crash

### Same Issue in MessageTransit

`MessageTransit` constructor accepts `int maxMessageSize` with no validation.
`maxMessageSize = -1` causes ALL messages to be blocked.

## CWE Reference

- [CWE-20: Improper Input Validation](https://cwe.mitre.org/data/definitions/20.html)
- [CWE-400: Uncontrolled Resource Consumption](https://cwe.mitre.org/data/definitions/400.html)

## Recommended Fix

Add validation in `MultiplexerOptions` using an `init` setter or a builder/validate method.
Enforce a sane range:

```csharp
public class MultiplexerOptions
{
    public const int MinAllowedFrameSize = 1024;       // 1 KB minimum
    public const int MaxAllowedFrameSize = 64 * 1024 * 1024; // 64 MB cap

    private int _maxFrameSize = 16 * 1024 * 1024;

    public int MaxFrameSize
    {
        get => _maxFrameSize;
        init
        {
            if (value < MinAllowedFrameSize || value > MaxAllowedFrameSize)
                throw new ArgumentOutOfRangeException(nameof(MaxFrameSize),
                    $"Must be between {MinAllowedFrameSize} and {MaxAllowedFrameSize}.");
            _maxFrameSize = value;
        }
    }
}
```

Apply the same pattern to `MessageTransit`:

```csharp
public MessageTransit(IStreamMultiplexer mux, string channelId, int maxMessageSize = DefaultMaxSize)
{
    if (maxMessageSize < 1)
        throw new ArgumentOutOfRangeException(nameof(maxMessageSize), "Must be positive.");
    // ...
}
```

Key points:
- Reject negative and zero values at construction time — fail fast
- Cap the upper bound to prevent multi-GB allocations from a malicious peer
- The `TryParseFrame` comparison becomes safe because `MaxFrameSize` is always positive

## Reproduction

See `MaxFrameSizeBypassTest.cs` — proves the behavior with various MaxFrameSize values.
