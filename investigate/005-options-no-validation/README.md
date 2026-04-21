# BUG: No Options Validation — MinCredits > MaxCredits Corrupts Flow Control

## Severity: MEDIUM

## Summary

`ChannelOptions` and `MultiplexerOptions` have **no validation** on their properties.
Setting `MinCredits > MaxCredits` causes `AdaptiveFlowControl.TryShrinkIfIdle()` to
grow the window size ABOVE `MaxCredits`, violating the invariant and leading to
unpredictable flow control behavior.

## Evidence

### Code Location

[src/NetConduit/Models/ChannelOptions.cs](../../src/NetConduit/Models/ChannelOptions.cs):
```csharp
public uint MinCredits { get; init; } = 64 * 1024;    // 64 KB
public uint MaxCredits { get; init; } = 4 * 1024 * 1024; // 4 MB
// No validation that MinCredits <= MaxCredits!
```

[src/NetConduit/Internal/AdaptiveFlowControl.cs](../../src/NetConduit/Internal/AdaptiveFlowControl.cs):
```csharp
public bool TryShrinkIfIdle()
{
    lock (_shrinkLock)
    {
        if (idleTime > ShrinkIdleMs && _currentWindowSize > _minCredits)
        {
            // BUG: Math.Max forces window UP to _minCredits
            // If _minCredits > _maxCredits, window exceeds max!
            var newSize = (uint)Math.Max(_currentWindowSize * ShrinkFactor, _minCredits);
            Volatile.Write(ref _currentWindowSize, newSize);
        }
    }
}
```

### Proof

With `MinCredits = 1_000_000` and `MaxCredits = 100`:
1. Window starts at `max(MaxCredits) = 100`
2. Channel goes idle for 5+ seconds
3. `TryShrinkIfIdle` fires: `newSize = Math.Max(100 * 0.5, 1_000_000) = 1_000_000`
4. Window is now **1,000,000** — which is **10,000x the MaxCredits**!
5. Receiver grants 1MB of credits for a channel that should only get 100 bytes
6. Sender sends 1MB of data before backpressure kicks in

## CWE Reference

- [CWE-20: Improper Input Validation](https://cwe.mitre.org/data/definitions/20.html)

## Recommended Fix

Add cross-property validation to `ChannelOptions`. Use either an `init` setter with
cross-validation or a `Validate()` method called at construction boundaries:

**Option A — Validate in `AdaptiveFlowControl` constructor (defensive):**

```csharp
internal AdaptiveFlowControl(uint minCredits, uint maxCredits)
{
    if (minCredits > maxCredits)
        throw new ArgumentException(
            $"MinCredits ({minCredits}) must not exceed MaxCredits ({maxCredits}).");

    if (maxCredits == 0)
        throw new ArgumentOutOfRangeException(nameof(maxCredits), "Must be greater than zero.");

    _minCredits = minCredits;
    _maxCredits = maxCredits;
    _currentWindowSize = maxCredits;
}
```

**Option B — Validate in `ChannelOptions` (fail-fast at source):**

```csharp
public class ChannelOptions
{
    // ...
    public void Validate()
    {
        if (MinCredits > MaxCredits)
            throw new InvalidOperationException(
                $"MinCredits ({MinCredits}) must not exceed MaxCredits ({MaxCredits}).");
    }
}
```

Call `Validate()` from `OpenChannelAsync` and `ProcessInitFrame` before constructing
the flow control instance.

Both options can coexist — Option B gives early diagnostics, Option A provides defense-in-depth.

## Reproduction

See `OptionsValidationTest.cs` — directly exercises the AdaptiveFlowControl logic.
